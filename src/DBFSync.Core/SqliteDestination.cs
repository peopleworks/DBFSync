using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PeopleWorks.DBFSync;

internal sealed class SqliteDestination : ITableDestination
{
    private const string RecordNumberColumn = "_dbfsync_recno";
    private const string HashColumn = "_dbfsync_hash";
    private const string SynchronizedAtColumn = "_dbfsync_at";

    private readonly SqliteConnection _connection;
    private readonly string _databasePath;
    private readonly string _stageName = "dbfsync_" + Guid.NewGuid().ToString("N");
    private readonly List<string> _schemaChanges = [];
    private DbfTableSchema? _table;
    private SchemaSyncOptions _schemaSync = new(false, false);
    private SqliteTransaction? _stageTransaction;
    private SqliteCommand? _stageInsert;
    private FileStream? _fileLock;
    private bool _recreate;
    private bool _completed;

    public SqliteDestination(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _databasePath = Path.GetFullPath(
            new SqliteConnectionStringBuilder(connectionString).DataSource);
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
        AcquireFileLock(table);
        await _connection.OpenAsync(cancellationToken);
        await ExecuteAsync(
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;",
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
        _stageTransaction = _connection.BeginTransaction();
        _stageInsert = CreateStageInsert(table, _stageTransaction);
    }

    public async Task WriteBatchAsync(
        IReadOnlyList<DbfRow> rows,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        SqliteCommand command = _stageInsert
            ?? throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));

        foreach (DbfRow row in rows)
        {
            command.Parameters["$recno"].Value = row.RecordNumber;
            command.Parameters["$hash"].Value = row.Hash;
            for (int i = 0; i < table.Columns.Count; i++)
            {
                command.Parameters["$c" + i].Value =
                    ToSqliteValue(row.Values[i], table.Columns[i].Kind);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<(long Inserted, long Updated, long Deleted)> CompleteAsync(
        TransferMode mode,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        if (_completed)
            throw new InvalidOperationException(L10n.T("TransferAlreadyCompleted"));
        if (_stageTransaction is null || _stageInsert is null)
            throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));

        await _stageTransaction.CommitAsync(cancellationToken);
        await _stageInsert.DisposeAsync();
        await _stageTransaction.DisposeAsync();
        _stageInsert = null;
        _stageTransaction = null;

        await using SqliteTransaction transaction =
            _connection.BeginTransaction(deferred: false);
        try
        {
            if (_recreate)
            {
                await ExecuteAsync(
                    $"DROP TABLE IF EXISTS {Quote(table.TargetName)};",
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
        if (_stageInsert is not null)
            await _stageInsert.DisposeAsync();
        if (_stageTransaction is not null)
            await _stageTransaction.DisposeAsync();
        await _connection.DisposeAsync();
        if (_fileLock is not null)
            await _fileLock.DisposeAsync();
    }

    private void AcquireFileLock(DbfTableSchema table)
    {
        try
        {
            _fileLock = new FileStream(
                _databasePath + ".dbfsync.lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                L10n.T("ConcurrentSynchronization", table.TargetName),
                ex);
        }
    }

    private async Task EnsureTargetAsync(CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        await using var command = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table;",
            _connection);
        command.Parameters.AddWithValue("$table", table.TargetName);
        long count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (count == 0)
            await ExecuteAsync(CreateTargetSql(table), transaction: null, cancellationToken);
    }

    private async Task CreateStageAsync(CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        string columns = string.Join(
            "," + Environment.NewLine,
            StageColumnDefinitions(table));
        await ExecuteAsync(
            $"CREATE TEMP TABLE {Quote(_stageName)} ({Environment.NewLine}" +
            $"{columns}{Environment.NewLine});",
            transaction: null,
            cancellationToken);
    }

    private SqliteCommand CreateStageInsert(
        DbfTableSchema table,
        SqliteTransaction transaction)
    {
        string parameters = string.Join(
            ", ",
            new[] { "$recno", "$hash" }
                .Concat(Enumerable.Range(0, table.Columns.Count).Select(i => "$c" + i)));
        var command = new SqliteCommand(
            $"INSERT INTO {Quote(_stageName)} ({JoinedTargetColumns(table)}) " +
            $"VALUES ({parameters});",
            _connection,
            transaction);
        command.Parameters.Add("$recno", SqliteType.Integer);
        command.Parameters.Add("$hash", SqliteType.Blob);
        for (int i = 0; i < table.Columns.Count; i++)
            command.Parameters.Add("$c" + i, ParameterType(table.Columns[i].Kind));
        command.Prepare();
        return command;
    }

    private async Task<(long Inserted, long Updated, long Deleted)> MigrateAsync(
        DbfTableSchema table,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        long deleted = await ScalarAsync(
            $"SELECT COUNT(*) FROM {Quote(table.TargetName)};",
            transaction,
            cancellationToken);
        await ExecuteAsync(
            $"DELETE FROM {Quote(table.TargetName)};",
            transaction,
            cancellationToken);
        await ExecuteAsync(
            $"INSERT INTO {Quote(table.TargetName)} " +
            $"({JoinedTargetColumns(table)}, {Quote(SynchronizedAtColumn)}) " +
            $"SELECT {JoinedStageColumns(table, "s")}, {UtcNowSql()} " +
            $"FROM {Quote(_stageName)} AS s;",
            transaction,
            cancellationToken);
        long inserted = await ScalarAsync(
            $"SELECT COUNT(*) FROM {Quote(_stageName)};",
            transaction,
            cancellationToken);
        return (inserted, 0, deleted);
    }

    private async Task<(long Inserted, long Updated, long Deleted)> SynchronizeAsync(
        DbfTableSchema table,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        string target = Quote(table.TargetName);
        string stage = Quote(_stageName);
        string recno = Quote(RecordNumberColumn);
        string hash = Quote(HashColumn);
        long inserted = await ScalarAsync(
            $"SELECT COUNT(*) FROM {stage} AS s " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {target} AS t WHERE t.{recno}=s.{recno});",
            transaction,
            cancellationToken);
        long updated = await ScalarAsync(
            $"SELECT COUNT(*) FROM {stage} AS s INNER JOIN {target} AS t " +
            $"ON t.{recno}=s.{recno} WHERE t.{hash}<>s.{hash};",
            transaction,
            cancellationToken);
        long deleted = await ScalarAsync(
            $"SELECT COUNT(*) FROM {target} AS t " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {stage} AS s WHERE s.{recno}=t.{recno});",
            transaction,
            cancellationToken);

        string updates = string.Join(
            ", ",
            table.Columns.Select(column =>
                $"{Quote(column.TargetName)}=excluded.{Quote(column.TargetName)}")
                .Append($"{hash}=excluded.{hash}")
                .Append($"{Quote(SynchronizedAtColumn)}=excluded.{Quote(SynchronizedAtColumn)}"));
        await ExecuteAsync(
            $"INSERT INTO {target} ({JoinedTargetColumns(table)}, {Quote(SynchronizedAtColumn)}) " +
            $"SELECT {JoinedStageColumns(table, "s")}, {UtcNowSql()} FROM {stage} AS s WHERE 1 " +
            $"ON CONFLICT({recno}) DO UPDATE SET {updates} " +
            $"WHERE {hash}<>excluded.{hash};",
            transaction,
            cancellationToken);
        await ExecuteAsync(
            $"DELETE FROM {target} AS t WHERE NOT EXISTS " +
            $"(SELECT 1 FROM {stage} AS s WHERE s.{recno}=t.{recno});",
            transaction,
            cancellationToken);
        return (inserted, updated, deleted);
    }

    private async Task ValidateTargetColumnsAsync(
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        Dictionary<string, string> actual =
            await ReadTargetColumnTypesAsync(transaction, cancellationToken);
        Dictionary<string, string> expected = ExpectedColumnTypes(table);
        if (actual.Count != expected.Count ||
            expected.Any(pair =>
                !actual.TryGetValue(pair.Key, out string? type) ||
                !type.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(L10n.T(
                "DestinationSchemaChanged",
                table.TargetName));
    }

    private async Task<Dictionary<string, string>> ReadTargetColumnTypesAsync(
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        await using var command = new SqliteCommand(
            $"PRAGMA table_info({Quote(table.TargetName)});",
            _connection,
            transaction);
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            actual.Add(reader.GetString(1), reader.GetString(2).ToUpperInvariant());
        return actual;
    }

    private async Task SynchronizeTargetSchemaAsync(
        SqliteTransaction transaction,
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
                !actualType.Equals(expected[name], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L10n.T(
                    "ManagedMetadataMissing",
                    table.TargetName));
        }

        foreach (DbfColumn column in table.Columns)
        {
            string expectedType = expected[column.TargetName];
            if (!actual.TryGetValue(column.TargetName, out string? actualType))
            {
                await ExecuteAsync(
                    $"ALTER TABLE {Quote(table.TargetName)} ADD COLUMN " +
                    $"{Quote(column.TargetName)} {expectedType} NULL;",
                    transaction,
                    cancellationToken);
                _schemaChanges.Add(L10n.T(
                    "SchemaAddedColumn",
                    table.TargetName,
                    column.TargetName,
                    expectedType));
                continue;
            }
            if (!actualType.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L10n.T(
                    "UnsafeTypeChange",
                    column.TargetName,
                    actualType,
                    expectedType));
        }

        string[] removed = actual.Keys
            .Where(name =>
                !expected.ContainsKey(name) &&
                !metadata.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (removed.Length > 0 && !_schemaSync.AllowDropColumns)
            throw new InvalidDataException(L10n.T(
                "DropColumnsRequired",
                string.Join(", ", removed)));

        foreach (string name in removed)
        {
            await ExecuteAsync(
                $"ALTER TABLE {Quote(table.TargetName)} DROP COLUMN {Quote(name)};",
                transaction,
                cancellationToken);
            _schemaChanges.Add(L10n.T(
                "SchemaDroppedColumn",
                table.TargetName,
                name));
        }
    }

    private string CreateTargetSql(DbfTableSchema table)
    {
        string columns = string.Join(
            "," + Environment.NewLine,
            TargetColumnDefinitions(table));
        return $"CREATE TABLE {Quote(table.TargetName)} ({Environment.NewLine}" +
               $"{columns}{Environment.NewLine});";
    }

    private static IEnumerable<string> StageColumnDefinitions(DbfTableSchema table)
    {
        yield return $"    {Quote(RecordNumberColumn)} INTEGER NOT NULL PRIMARY KEY";
        yield return $"    {Quote(HashColumn)} BLOB NOT NULL";
        foreach (DbfColumn column in table.Columns)
            yield return $"    {Quote(column.TargetName)} {SqlTypeMapper.SQLite(column)} NULL";
    }

    private static IEnumerable<string> TargetColumnDefinitions(DbfTableSchema table)
    {
        yield return $"    {Quote(RecordNumberColumn)} INTEGER NOT NULL PRIMARY KEY";
        yield return $"    {Quote(HashColumn)} BLOB NOT NULL";
        foreach (DbfColumn column in table.Columns)
            yield return $"    {Quote(column.TargetName)} {SqlTypeMapper.SQLite(column)} NULL";
        yield return $"    {Quote(SynchronizedAtColumn)} TEXT NOT NULL";
    }

    private static Dictionary<string, string> ExpectedColumnTypes(DbfTableSchema table)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RecordNumberColumn] = "INTEGER",
            [HashColumn] = "BLOB",
            [SynchronizedAtColumn] = "TEXT"
        };
        foreach (DbfColumn column in table.Columns)
            expected.Add(column.TargetName, SqlTypeMapper.SQLite(column));
        return expected;
    }

    private static object ToSqliteValue(object? value, DbfColumnKind kind)
    {
        if (value is null)
            return DBNull.Value;
        return kind switch
        {
            DbfColumnKind.Decimal => ((decimal)value).ToString(CultureInfo.InvariantCulture),
            DbfColumnKind.Boolean => (bool)value ? 1L : 0L,
            DbfColumnKind.Date => ((DateTime)value).ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            DbfColumnKind.DateTime => ((DateTime)value).ToString(
                "yyyy-MM-ddTHH:mm:ss.fff",
                CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static SqliteType ParameterType(DbfColumnKind kind) =>
        kind switch
        {
            DbfColumnKind.Text or DbfColumnKind.Decimal or
                DbfColumnKind.Date or DbfColumnKind.DateTime => SqliteType.Text,
            DbfColumnKind.Integer or DbfColumnKind.Boolean => SqliteType.Integer,
            DbfColumnKind.Double => SqliteType.Real,
            DbfColumnKind.Binary => SqliteType.Blob,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private async Task<long> ScalarAsync(
        string sql,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, _connection, transaction);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, _connection, transaction)
        {
            CommandTimeout = 0
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string JoinedTargetColumns(DbfTableSchema table) =>
        string.Join(
            ", ",
            new[] { RecordNumberColumn, HashColumn }
                .Concat(table.Columns.Select(column => column.TargetName))
                .Select(Quote));

    private static string JoinedStageColumns(DbfTableSchema table, string alias) =>
        string.Join(
            ", ",
            new[] { RecordNumberColumn, HashColumn }
                .Concat(table.Columns.Select(column => column.TargetName))
                .Select(name => $"{alias}.{Quote(name)}"));

    private static string UtcNowSql() =>
        "strftime('%Y-%m-%dT%H:%M:%fZ','now')";

    private DbfTableSchema RequiredTable() =>
        _table ?? throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
}
