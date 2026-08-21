using System.Data;
using Microsoft.Data.SqlClient;

namespace PeopleWorks.DBFSync;

internal sealed class SqlServerDestination : ITableDestination
{
    private const string RecordNumberColumn = "_dbfsync_recno";
    private const string HashColumn = "_dbfsync_hash";
    private const string SynchronizedAtColumn = "_dbfsync_at";

    private readonly SqlConnection _connection;
    private readonly string _schemaName;
    private readonly string _stageName = "#dbfsync_" + Guid.NewGuid().ToString("N");
    private readonly List<string> _schemaChanges = [];
    private DbfTableSchema? _table;
    private DataTable? _batchTable;
    private SchemaSyncOptions _schemaSync = new(false, false);
    private bool _lockAcquired;
    private bool _recreate;
    private bool _completed;

    public SqlServerDestination(string connectionString, string schemaName)
    {
        _connection = new SqlConnection(connectionString);
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
        await EnsureSchemaAsync(cancellationToken);
        if (!recreate)
        {
            await EnsureTargetAsync(cancellationToken);
            if (!schemaSync.Enabled)
                await ValidateTargetColumnsAsync(
                    transaction: null,
                    cancellationToken);
        }
        await CreateStageAsync(cancellationToken);
        _batchTable = CreateBatchTable(table);
    }

    public async Task WriteBatchAsync(
        IReadOnlyList<DbfRow> rows,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        DataTable data = _batchTable
            ?? throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));
        if (rows.Count == 0)
            return;

        foreach (DbfRow row in rows)
        {
            DataRow dataRow = data.NewRow();
            dataRow[RecordNumberColumn] = row.RecordNumber;
            dataRow[HashColumn] = row.Hash;
            for (int i = 0; i < table.Columns.Count; i++)
                dataRow[table.Columns[i].TargetName] = row.Values[i] ?? DBNull.Value;
            data.Rows.Add(dataRow);
        }

        using var bulkCopy = new SqlBulkCopy(
            _connection,
            SqlBulkCopyOptions.TableLock,
            externalTransaction: null)
        {
            DestinationTableName = SqlIdentifier.SqlServer(_stageName),
            EnableStreaming = true,
            BatchSize = rows.Count,
            BulkCopyTimeout = 0
        };
        foreach (DataColumn column in data.Columns)
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulkCopy.WriteToServerAsync(data, cancellationToken);
        data.Clear();
    }

    public async Task<(long Inserted, long Updated, long Deleted)> CompleteAsync(
        TransferMode mode,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        if (_completed)
            throw new InvalidOperationException(L10n.T("TransferAlreadyCompleted"));

        await using SqlTransaction transaction =
            (SqlTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (_recreate)
            {
                await ExecuteAsync(
                    $"IF OBJECT_ID(N'{QualifiedLiteral(table)}', N'U') IS NOT NULL " +
                    $"DROP TABLE {Qualified(table)};",
                    transaction,
                    cancellationToken);
                await ExecuteAsync(CreateTargetSql(table), transaction, cancellationToken);
            }
            else if (_schemaSync.Enabled)
            {
                await SynchronizeTargetSchemaAsync(transaction, cancellationToken);
                await ValidateTargetColumnsAsync(transaction, cancellationToken);
            }

            string sql = mode == TransferMode.Migrate
                ? BuildMigrateSql(table)
                : BuildSynchronizeSql(table);
            await using var command = new SqlCommand(sql, _connection, transaction)
            {
                CommandTimeout = 0
            };
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidDataException(L10n.T(
                    "SynchronizationResultMissing",
                    "SQL Server"));
            long inserted = reader.GetInt64(0);
            long updated = reader.GetInt64(1);
            long deleted = reader.GetInt64(2);
            await transaction.CommitAsync(cancellationToken);
            _completed = true;
            return (inserted, updated, deleted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lockAcquired && _connection.State == ConnectionState.Open)
        {
            await using var command = new SqlCommand(
                """
                DECLARE @result int;
                EXEC @result = sys.sp_releaseapplock
                    @Resource = @resource,
                    @LockOwner = 'Session';
                SELECT @result;
                """,
                _connection);
            command.Parameters.AddWithValue("@resource", LockResource());
            _ = await command.ExecuteScalarAsync();
            _lockAcquired = false;
        }
        await _connection.DisposeAsync();
        _batchTable?.Dispose();
    }

    private async Task AcquireLockAsync(CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 0;
            SELECT @result;
            """,
            _connection);
        command.Parameters.AddWithValue("@resource", LockResource());
        int result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (result < 0)
            throw new InvalidOperationException(L10n.T(
                "ConcurrentSynchronization",
                RequiredTable().TargetName));
        _lockAcquired = true;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF SCHEMA_ID(@schema) IS NULL
                EXEC(N'CREATE SCHEMA ' + QUOTENAME(@schema));
            """,
            _connection);
        command.Parameters.AddWithValue("@schema", _schemaName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureTargetAsync(CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        await using var exists = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table;
            """,
            _connection);
        exists.Parameters.AddWithValue("@schema", _schemaName);
        exists.Parameters.AddWithValue("@table", table.TargetName);
        int count = Convert.ToInt32(
            await exists.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count == 0)
            await ExecuteAsync(CreateTargetSql(table), transaction: null, cancellationToken);
    }

    private async Task ValidateTargetColumnsAsync(
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        Dictionary<string, string> actual =
            await ReadTargetColumnTypesAsync(transaction, cancellationToken);
        Dictionary<string, string> expected = ExpectedColumnTypes(table);
        if (actual.Count != expected.Count ||
            expected.Any(pair =>
                !actual.TryGetValue(pair.Key, out string? actualType) ||
                !actualType.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(L10n.T(
                "DestinationSchemaChanged",
                Qualified(table)));
    }

    private async Task<Dictionary<string, string>> ReadTargetColumnTypesAsync(
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        DbfTableSchema table = RequiredTable();
        await using var command = new SqlCommand(
            """
            SELECT c.name, ty.name, c.max_length, c.precision, c.scale
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE s.name = @schema AND t.name = @table;
            """,
            _connection,
            transaction);
        command.Parameters.AddWithValue("@schema", _schemaName);
        command.Parameters.AddWithValue("@table", table.TargetName);
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string name = reader.GetString(0);
            string type = reader.GetString(1);
            short maxLength = reader.GetInt16(2);
            byte precision = reader.GetByte(3);
            byte scale = reader.GetByte(4);
            actual.Add(name, SqlServerCatalogType(type, maxLength, precision, scale));
        }
        return actual;
    }

    private async Task SynchronizeTargetSchemaAsync(
        SqlTransaction transaction,
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
                    Qualified(table)));
        }

        foreach (DbfColumn column in table.Columns)
        {
            string expectedType = expected[column.TargetName];
            if (!actual.TryGetValue(column.TargetName, out string? actualType))
            {
                await ExecuteAsync(
                    $"ALTER TABLE {Qualified(table)} ADD " +
                    $"{SqlIdentifier.SqlServer(column.TargetName)} {expectedType} NULL;",
                    transaction,
                    cancellationToken);
                _schemaChanges.Add(L10n.T(
                    "SchemaAddedColumn",
                    table.TargetName,
                    column.TargetName,
                    expectedType));
                continue;
            }

            if (actualType.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!SchemaTypeCompatibility.CanWiden(
                    DatabaseEngine.SqlServer,
                    actualType,
                    expectedType))
                throw new InvalidDataException(L10n.T(
                    "UnsafeTypeChange",
                    column.TargetName,
                    actualType,
                    expectedType));

            await ExecuteAsync(
                $"ALTER TABLE {Qualified(table)} ALTER COLUMN " +
                $"{SqlIdentifier.SqlServer(column.TargetName)} {expectedType} NULL;",
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
                !metadata.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (removed.Length > 0 && !_schemaSync.AllowDropColumns)
            throw new InvalidDataException(L10n.T(
                "DropColumnsRequired",
                string.Join(", ", removed)));

        foreach (string name in removed)
        {
            await ExecuteAsync(
                $"ALTER TABLE {Qualified(table)} DROP COLUMN {SqlIdentifier.SqlServer(name)};",
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
            StageColumnDefinitions(table, SqlIdentifier.SqlServer, SqlTypeMapper.SqlServer));
        await ExecuteAsync(
            $"CREATE TABLE {SqlIdentifier.SqlServer(_stageName)} ({Environment.NewLine}{columns}{Environment.NewLine});",
            transaction: null,
            cancellationToken);
    }

    private string CreateTargetSql(DbfTableSchema table)
    {
        string columns = string.Join(
            "," + Environment.NewLine,
            TargetColumnDefinitions(table, SqlIdentifier.SqlServer, SqlTypeMapper.SqlServer));
        return $"CREATE TABLE {Qualified(table)} ({Environment.NewLine}{columns}{Environment.NewLine});";
    }

    private string BuildMigrateSql(DbfTableSchema table)
    {
        string target = Qualified(table);
        string stage = SqlIdentifier.SqlServer(_stageName);
        string targetColumns = JoinedTargetColumns(table, SqlIdentifier.SqlServer);
        string stageColumns = JoinedStageColumns(table, "s", SqlIdentifier.SqlServer);
        return $"""
                SET NOCOUNT ON;
                DECLARE @deleted bigint = (SELECT COUNT_BIG(*) FROM {target});
                DELETE FROM {target};
                INSERT INTO {target} ({targetColumns}, {SqlIdentifier.SqlServer(SynchronizedAtColumn)})
                SELECT {stageColumns}, SYSUTCDATETIME()
                FROM {stage} AS s;
                DECLARE @inserted bigint = @@ROWCOUNT;
                SELECT @inserted, CAST(0 AS bigint), @deleted;
                """;
    }

    private string BuildSynchronizeSql(DbfTableSchema table)
    {
        string target = Qualified(table);
        string stage = SqlIdentifier.SqlServer(_stageName);
        string assignments = string.Join(
            ", ",
            table.Columns.Select(column =>
                $"{SqlIdentifier.SqlServer(column.TargetName)} = s.{SqlIdentifier.SqlServer(column.TargetName)}")
                .Append(
                    $"{SqlIdentifier.SqlServer(HashColumn)} = s.{SqlIdentifier.SqlServer(HashColumn)}")
                .Append($"{SqlIdentifier.SqlServer(SynchronizedAtColumn)} = SYSUTCDATETIME()"));
        string targetColumns = JoinedTargetColumns(table, SqlIdentifier.SqlServer);
        string stageColumns = JoinedStageColumns(table, "s", SqlIdentifier.SqlServer);
        string recordNumber = SqlIdentifier.SqlServer(RecordNumberColumn);
        string hash = SqlIdentifier.SqlServer(HashColumn);

        return $"""
                SET NOCOUNT ON;
                DECLARE @updated bigint, @inserted bigint, @deleted bigint;

                UPDATE t
                SET {assignments}
                FROM {target} AS t
                INNER JOIN {stage} AS s ON s.{recordNumber} = t.{recordNumber}
                WHERE s.{hash} <> t.{hash};
                SET @updated = @@ROWCOUNT;

                INSERT INTO {target} ({targetColumns}, {SqlIdentifier.SqlServer(SynchronizedAtColumn)})
                SELECT {stageColumns}, SYSUTCDATETIME()
                FROM {stage} AS s
                WHERE NOT EXISTS (
                    SELECT 1 FROM {target} AS t WHERE t.{recordNumber} = s.{recordNumber}
                );
                SET @inserted = @@ROWCOUNT;

                DELETE t
                FROM {target} AS t
                WHERE NOT EXISTS (
                    SELECT 1 FROM {stage} AS s WHERE s.{recordNumber} = t.{recordNumber}
                );
                SET @deleted = @@ROWCOUNT;

                SELECT @inserted, @updated, @deleted;
                """;
    }

    private static DataTable CreateBatchTable(DbfTableSchema table)
    {
        var data = new DataTable();
        data.Columns.Add(RecordNumberColumn, typeof(long));
        data.Columns.Add(HashColumn, typeof(byte[]));
        foreach (DbfColumn column in table.Columns)
        {
            DataColumn dataColumn = data.Columns.Add(
                column.TargetName,
                SqlTypeMapper.DataColumnType(column));
            dataColumn.AllowDBNull = true;
        }
        return data;
    }

    private static IEnumerable<string> StageColumnDefinitions(
        DbfTableSchema table,
        Func<string, string> quote,
        Func<DbfColumn, string> mapType)
    {
        yield return $"    {quote(RecordNumberColumn)} bigint NOT NULL PRIMARY KEY";
        yield return $"    {quote(HashColumn)} varbinary(32) NOT NULL";
        foreach (DbfColumn column in table.Columns)
            yield return $"    {quote(column.TargetName)} {mapType(column)} NULL";
    }

    private static IEnumerable<string> TargetColumnDefinitions(
        DbfTableSchema table,
        Func<string, string> quote,
        Func<DbfColumn, string> mapType)
    {
        yield return $"    {quote(RecordNumberColumn)} bigint NOT NULL PRIMARY KEY";
        yield return $"    {quote(HashColumn)} varbinary(32) NOT NULL";
        foreach (DbfColumn column in table.Columns)
            yield return $"    {quote(column.TargetName)} {mapType(column)} NULL";
        yield return $"    {quote(SynchronizedAtColumn)} datetime2(3) NOT NULL";
    }

    private static Dictionary<string, string> ExpectedColumnTypes(DbfTableSchema table)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RecordNumberColumn] = "bigint",
            [HashColumn] = "varbinary(32)",
            [SynchronizedAtColumn] = "datetime2(3)"
        };
        foreach (DbfColumn column in table.Columns)
            expected.Add(column.TargetName, SqlTypeMapper.SqlServer(column));
        return expected;
    }

    private static string SqlServerCatalogType(
        string type,
        short maxLength,
        byte precision,
        byte scale) =>
        type.ToLowerInvariant() switch
        {
            "nvarchar" => maxLength < 0
                ? "nvarchar(max)"
                : $"nvarchar({maxLength / 2})",
            "varbinary" => maxLength < 0
                ? "varbinary(max)"
                : $"varbinary({maxLength})",
            "decimal" or "numeric" => $"decimal({precision}, {scale})",
            "float" => $"float({precision})",
            "datetime2" => $"datetime2({scale})",
            _ => type.ToLowerInvariant()
        };

    private static string JoinedTargetColumns(
        DbfTableSchema table,
        Func<string, string> quote) =>
        string.Join(
            ", ",
            new[] { RecordNumberColumn, HashColumn }
                .Concat(table.Columns.Select(column => column.TargetName))
                .Select(quote));

    private static string JoinedStageColumns(
        DbfTableSchema table,
        string alias,
        Func<string, string> quote) =>
        string.Join(
            ", ",
            new[] { RecordNumberColumn, HashColumn }
                .Concat(table.Columns.Select(column => column.TargetName))
                .Select(name => $"{alias}.{quote(name)}"));

    private async Task ExecuteAsync(
        string sql,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, _connection, transaction)
        {
            CommandTimeout = 0
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbfTableSchema RequiredTable() =>
        _table ?? throw new InvalidOperationException(L10n.T("DestinationNotPrepared"));

    private string Qualified(DbfTableSchema table) =>
        $"{SqlIdentifier.SqlServer(_schemaName)}.{SqlIdentifier.SqlServer(table.TargetName)}";

    private string QualifiedLiteral(DbfTableSchema table) =>
        $"{_schemaName.Replace("'", "''")}.{table.TargetName.Replace("'", "''")}";

    private string LockResource() =>
        $"PeopleWorks.DBFSync:{_schemaName}.{RequiredTable().TargetName}";
}
