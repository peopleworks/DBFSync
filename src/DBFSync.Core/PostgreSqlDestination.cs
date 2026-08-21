using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PeopleWorks.DBFSync;

internal sealed class PostgreSqlDestination : ITableDestination
{
    private const string RecordNumberColumn = "_dbfsync_recno";
    private const string HashColumn = "_dbfsync_hash";
    private const string SynchronizedAtColumn = "_dbfsync_at";

    private readonly NpgsqlConnection _connection;
    private readonly string _schemaName;
    private readonly string _stageName = "dbfsync_" + Guid.NewGuid().ToString("N");
    private readonly List<string> _schemaChanges = [];
    private DbfTableSchema? _table;
    private NpgsqlBinaryImporter? _importer;
    private SchemaSyncOptions _schemaSync = new(false, false);
    private bool _lockAcquired;
    private bool _recreate;
    private bool _completed;

    public PostgreSqlDestination(string connectionString, string schemaName)
    {
        _connection = new NpgsqlConnection(connectionString);
        _schemaName = schemaName;
    }

    public IReadOnlyList<string> SchemaChanges => _schemaChanges;

    public async Task PrepareAsync(
        DbfTableSchema table,
        bool recreate,
        SchemaSyncOptions schemaSync,
        CancellationToken cancellationToken)
    {
        if (_table is not null)
            throw new InvalidOperationException(L10n.T("DestinationAlreadyPrepared"));

        _table = table;
        _recreate = recreate;
        _schemaSync = schemaSync;
        await _connection.OpenAsync(cancellationToken);
        await AcquireLockAsync(cancellationToken);
        await ExecuteAsync(
            $"CREATE SCHEMA IF NOT EXISTS {SqlIdentifier.PostgreSql(_schemaName)};",
            transaction: null,
            cancellationToken);
        if (!recreate)
        {
            await EnsureTargetAsync(cancellationToken);
            if (!schemaSync.Enabled)
                await ValidateTargetColumnsAsync(
                    transaction: null,
                    cancellationToken);
        }
        await CreateStageAsync(cancellationToken);
        _importer = await _connection.BeginBinaryImportAsync(
            BuildCopySql(table),
            cancellationToken);
    }

    public async Task WriteBatchAsync(
        IReadOnlyList<DbfRow> rows,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        NpgsqlBinaryImporter importer = _importer
            ?? throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));

        foreach (DbfRow row in rows)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(
                row.RecordNumber,
                NpgsqlDbType.Bigint,
                cancellationToken);
            await importer.WriteAsync(row.Hash, NpgsqlDbType.Bytea, cancellationToken);
            for (int i = 0; i < table.Columns.Count; i++)
                await WriteValueAsync(
                    importer,
                    row.Values[i],
                    table.Columns[i].Kind,
                    cancellationToken);
        }
    }

    public async Task<(long Inserted, long Updated, long Deleted)> CompleteAsync(
        TransferMode mode,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        if (_completed)
            throw new InvalidOperationException(L10n.T("TransferAlreadyCompleted"));

        NpgsqlBinaryImporter importer = _importer
            ?? throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));
        await importer.CompleteAsync(cancellationToken);
        await importer.DisposeAsync();
        _importer = null;

        await using NpgsqlTransaction transaction =
            await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (_recreate)
            {
                await ExecuteAsync(
                    $"DROP TABLE IF EXISTS {Qualified(table)};",
                    transaction,
                    cancellationToken);
                await ExecuteAsync(CreateTargetSql(table), transaction, cancellationToken);
            }
            else if (_schemaSync.Enabled)
            {
                await SynchronizeTargetSchemaAsync(transaction, cancellationToken);
                await ValidateTargetColumnsAsync(transaction, cancellationToken);
            }

            (long Inserted, long Updated, long Deleted) result =
                mode == TransferMode.Migrate
                    ? await MigrateAsync(table, transaction, cancellationToken)
                    : await SynchronizeAsync(table, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _completed = true;
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_importer is not null)
        {
            await _importer.DisposeAsync();
            _importer = null;
        }
        if (_lockAcquired && _connection.State == ConnectionState.Open)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@resource, 0));",
                _connection);
            command.Parameters.AddWithValue("resource", LockResource());
            object? released = await command.ExecuteScalarAsync();
            if (released is not true)
                throw new InvalidOperationException(L10n.T(
                    "PostgresLockReleaseFailed",
                    RequiredTable().TargetName));
            _lockAcquired = false;
        }
        await _connection.DisposeAsync();
    }

    private async Task AcquireLockAsync(CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtextextended(@resource, 0));",
            _connection);
        command.Parameters.AddWithValue("resource", LockResource());
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not true)
            throw new InvalidOperationException(L10n.T(
                "ConcurrentSynchronization",
                RequiredTable().TargetName));
        _lockAcquired = true;
    }

    private async Task EnsureTargetAsync(CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        await using var exists = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table;
            """,
            _connection);
        exists.Parameters.AddWithValue("schema", _schemaName);
        exists.Parameters.AddWithValue("table", table.TargetName);
        long count = Convert.ToInt64(
            await exists.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count == 0)
            await ExecuteAsync(CreateTargetSql(table), transaction: null, cancellationToken);
    }

    private async Task ValidateTargetColumnsAsync(
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        Dictionary<string, string> actual =
            await ReadTargetColumnTypesAsync(transaction, cancellationToken);
        Dictionary<string, string> expected = ExpectedColumnTypes(table);
        if (actual.Count != expected.Count ||
            expected.Any(pair =>
                !actual.TryGetValue(pair.Key, out string? actualType) ||
                !actualType.Equals(pair.Value, StringComparison.Ordinal)))
            throw new InvalidDataException(L10n.T(
                "DestinationSchemaChanged",
                Qualified(table)));
    }

    private async Task<Dictionary<string, string>> ReadTargetColumnTypesAsync(
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        await using var command = new NpgsqlCommand(
            """
            SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod)
            FROM pg_catalog.pg_attribute AS a
            INNER JOIN pg_catalog.pg_class AS c ON c.oid = a.attrelid
            INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relname = @table
              AND a.attnum > 0
              AND NOT a.attisdropped;
            """,
            _connection,
            transaction);
        command.Parameters.AddWithValue("schema", _schemaName);
        command.Parameters.AddWithValue("table", table.TargetName);
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            actual.Add(reader.GetString(0), reader.GetString(1));
        return actual;
    }

    private async Task SynchronizeTargetSchemaAsync(
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        Dictionary<string, string> actual =
            await ReadTargetColumnTypesAsync(transaction, cancellationToken);
        Dictionary<string, string> expected = ExpectedColumnTypes(table);
        string[] metadata = [RecordNumberColumn, HashColumn, SynchronizedAtColumn];

        foreach (string name in metadata)
        {
            if (!actual.TryGetValue(name, out string? actualType) ||
                !actualType.Equals(expected[name], StringComparison.Ordinal))
                throw new InvalidDataException(L10n.T(
                    "ManagedMetadataMissing",
                    Qualified(table)));
        }

        foreach (DbfColumn column in table.Columns)
        {
            string expectedType = expected[column.TargetName];
            if (!actual.TryGetValue(column.TargetName, out string? actualType))
            {
                await ExecuteAsync(
                    $"ALTER TABLE {Qualified(table)} ADD COLUMN " +
                    $"{SqlIdentifier.PostgreSql(column.TargetName)} {SqlTypeMapper.PostgreSql(column)} NULL;",
                    transaction,
                    cancellationToken);
                _schemaChanges.Add(L10n.T(
                    "SchemaAddedColumn",
                    table.TargetName,
                    column.TargetName,
                    expectedType));
                continue;
            }

            if (actualType.Equals(expectedType, StringComparison.Ordinal))
                continue;
            if (!SchemaTypeCompatibility.CanWiden(
                    DatabaseEngine.PostgreSql,
                    actualType,
                    expectedType))
                throw new InvalidDataException(L10n.T(
                    "UnsafeTypeChange",
                    column.TargetName,
                    actualType,
                    expectedType));

            string targetType = SqlTypeMapper.PostgreSql(column);
            await ExecuteAsync(
                $"ALTER TABLE {Qualified(table)} ALTER COLUMN " +
                $"{SqlIdentifier.PostgreSql(column.TargetName)} TYPE {targetType} " +
                $"USING {SqlIdentifier.PostgreSql(column.TargetName)}::{targetType};",
                transaction,
                cancellationToken);
            _schemaChanges.Add(L10n.T(
                "SchemaAlteredColumn",
                table.TargetName,
                column.TargetName,
                actualType,
                expectedType));
        }

        string[] removed = actual.Keys
            .Where(name =>
                !expected.ContainsKey(name) &&
                !metadata.Contains(name, StringComparer.Ordinal))
            .ToArray();
        if (removed.Length > 0 && !_schemaSync.AllowDropColumns)
            throw new InvalidDataException(L10n.T(
                "DropColumnsRequired",
                string.Join(", ", removed)));

        foreach (string name in removed)
        {
            await ExecuteAsync(
                $"ALTER TABLE {Qualified(table)} DROP COLUMN " +
                $"{SqlIdentifier.PostgreSql(name)};",
                transaction,
                cancellationToken);
            _schemaChanges.Add(L10n.T(
                "SchemaDroppedColumn",
                table.TargetName,
                name));
        }
    }

    private async Task CreateStageAsync(CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        string columns = string.Join(
            "," + Environment.NewLine,
            StageColumnDefinitions(table));
        await ExecuteAsync(
            $"CREATE TEMP TABLE {SqlIdentifier.PostgreSql(_stageName)} ({Environment.NewLine}" +
            $"{columns}{Environment.NewLine}) ON COMMIT PRESERVE ROWS;",
            transaction: null,
            cancellationToken);
    }

    private async Task<(long Inserted, long Updated, long Deleted)> MigrateAsync(
        DbfTableSchema table,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        string target = Qualified(table);
        await using var countCommand = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {target};",
            _connection,
            transaction);
        long deleted = Convert.ToInt64(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        await ExecuteAsync($"DELETE FROM {target};", transaction, cancellationToken);

        string targetColumns = JoinedTargetColumns(table);
        string stageColumns = JoinedStageColumns(table, "s");
        await using var insert = new NpgsqlCommand(
            $"""
             INSERT INTO {target} ({targetColumns}, {SqlIdentifier.PostgreSql(SynchronizedAtColumn)})
             SELECT {stageColumns}, CURRENT_TIMESTAMP
             FROM {SqlIdentifier.PostgreSql(_stageName)} AS s;
             """,
            _connection,
            transaction)
        {
            CommandTimeout = 0
        };
        long inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        return (inserted, 0, deleted);
    }

    private async Task<(long Inserted, long Updated, long Deleted)> SynchronizeAsync(
        DbfTableSchema table,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        string target = Qualified(table);
        string stage = SqlIdentifier.PostgreSql(_stageName);
        string recordNumber = SqlIdentifier.PostgreSql(RecordNumberColumn);
        string hash = SqlIdentifier.PostgreSql(HashColumn);
        string assignments = string.Join(
            ", ",
            table.Columns.Select(column =>
                $"{SqlIdentifier.PostgreSql(column.TargetName)} = s.{SqlIdentifier.PostgreSql(column.TargetName)}")
                .Append(
                    $"{hash} = s.{hash}")
                .Append(
                    $"{SqlIdentifier.PostgreSql(SynchronizedAtColumn)} = CURRENT_TIMESTAMP"));
        string targetColumns = JoinedTargetColumns(table);
        string stageColumns = JoinedStageColumns(table, "s");

        string sql = $"""
                     WITH updated AS (
                         UPDATE {target} AS t
                         SET {assignments}
                         FROM {stage} AS s
                         WHERE s.{recordNumber} = t.{recordNumber}
                           AND s.{hash} IS DISTINCT FROM t.{hash}
                         RETURNING 1
                     ),
                     inserted AS (
                         INSERT INTO {target} (
                             {targetColumns}, {SqlIdentifier.PostgreSql(SynchronizedAtColumn)}
                         )
                         SELECT {stageColumns}, CURRENT_TIMESTAMP
                         FROM {stage} AS s
                         WHERE NOT EXISTS (
                             SELECT 1 FROM {target} AS t
                             WHERE t.{recordNumber} = s.{recordNumber}
                         )
                         RETURNING 1
                     ),
                     deleted AS (
                         DELETE FROM {target} AS t
                         WHERE NOT EXISTS (
                             SELECT 1 FROM {stage} AS s
                             WHERE s.{recordNumber} = t.{recordNumber}
                         )
                         RETURNING 1
                     )
                     SELECT
                         (SELECT COUNT(*) FROM inserted),
                         (SELECT COUNT(*) FROM updated),
                         (SELECT COUNT(*) FROM deleted);
                     """;
        await using var command = new NpgsqlCommand(sql, _connection, transaction)
        {
            CommandTimeout = 0
        };
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException(L10n.T(
                "SynchronizationResultMissing",
                "PostgreSQL"));
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private string CreateTargetSql(DbfTableSchema table)
    {
        string columns = string.Join(
            "," + Environment.NewLine,
            TargetColumnDefinitions(table));
        return $"CREATE TABLE {Qualified(table)} ({Environment.NewLine}{columns}{Environment.NewLine});";
    }

    private string BuildCopySql(DbfTableSchema table) =>
        $"COPY {SqlIdentifier.PostgreSql(_stageName)} ({JoinedTargetColumns(table)}) " +
        "FROM STDIN (FORMAT BINARY)";

    private static async Task WriteValueAsync(
        NpgsqlBinaryImporter importer,
        object? value,
        DbfColumnKind kind,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(cancellationToken);
            return;
        }

        switch (kind)
        {
            case DbfColumnKind.Text:
                await importer.WriteAsync((string)value, NpgsqlDbType.Text, cancellationToken);
                break;
            case DbfColumnKind.Decimal:
                await importer.WriteAsync((decimal)value, NpgsqlDbType.Numeric, cancellationToken);
                break;
            case DbfColumnKind.Integer:
                await importer.WriteAsync((long)value, NpgsqlDbType.Bigint, cancellationToken);
                break;
            case DbfColumnKind.Double:
                await importer.WriteAsync((double)value, NpgsqlDbType.Double, cancellationToken);
                break;
            case DbfColumnKind.Boolean:
                await importer.WriteAsync((bool)value, NpgsqlDbType.Boolean, cancellationToken);
                break;
            case DbfColumnKind.Date:
                await importer.WriteAsync(
                    DateOnly.FromDateTime((DateTime)value),
                    NpgsqlDbType.Date,
                    cancellationToken);
                break;
            case DbfColumnKind.DateTime:
                await importer.WriteAsync(
                    (DateTime)value,
                    NpgsqlDbType.Timestamp,
                    cancellationToken);
                break;
            case DbfColumnKind.Binary:
                await importer.WriteAsync((byte[])value, NpgsqlDbType.Bytea, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static IEnumerable<string> StageColumnDefinitions(DbfTableSchema table)
    {
        yield return
            $"    {SqlIdentifier.PostgreSql(RecordNumberColumn)} bigint NOT NULL PRIMARY KEY";
        yield return $"    {SqlIdentifier.PostgreSql(HashColumn)} bytea NOT NULL";
        foreach (DbfColumn column in table.Columns)
            yield return $"    {SqlIdentifier.PostgreSql(column.TargetName)} {SqlTypeMapper.PostgreSql(column)} NULL";
    }

    private static IEnumerable<string> TargetColumnDefinitions(DbfTableSchema table)
    {
        yield return $"    {SqlIdentifier.PostgreSql(RecordNumberColumn)} bigint PRIMARY KEY";
        yield return $"    {SqlIdentifier.PostgreSql(HashColumn)} bytea NOT NULL";
        foreach (DbfColumn column in table.Columns)
            yield return $"    {SqlIdentifier.PostgreSql(column.TargetName)} {SqlTypeMapper.PostgreSql(column)} NULL";
        yield return
            $"    {SqlIdentifier.PostgreSql(SynchronizedAtColumn)} timestamp with time zone NOT NULL";
    }

    private static Dictionary<string, string> ExpectedColumnTypes(DbfTableSchema table)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RecordNumberColumn] = "bigint",
            [HashColumn] = "bytea",
            [SynchronizedAtColumn] = "timestamp with time zone"
        };
        foreach (DbfColumn column in table.Columns)
            expected.Add(column.TargetName, SqlTypeMapper.PostgreSqlCatalog(column));
        return expected;
    }

    private static string JoinedTargetColumns(DbfTableSchema table) =>
        string.Join(
            ", ",
            new[] { RecordNumberColumn, HashColumn }
                .Concat(table.Columns.Select(column => column.TargetName))
                .Select(SqlIdentifier.PostgreSql));

    private static string JoinedStageColumns(DbfTableSchema table, string alias) =>
        string.Join(
            ", ",
            new[] { RecordNumberColumn, HashColumn }
                .Concat(table.Columns.Select(column => column.TargetName))
                .Select(name => $"{alias}.{SqlIdentifier.PostgreSql(name)}"));

    private async Task ExecuteAsync(
        string sql,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, _connection, transaction)
        {
            CommandTimeout = 0
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbfTableSchema RequiredTable() =>
        _table ?? throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));

    private string Qualified(DbfTableSchema table) =>
        $"{SqlIdentifier.PostgreSql(_schemaName)}.{SqlIdentifier.PostgreSql(table.TargetName)}";

    private string LockResource() =>
        $"PeopleWorks.DBFSync:{_schemaName}.{RequiredTable().TargetName}";
}
