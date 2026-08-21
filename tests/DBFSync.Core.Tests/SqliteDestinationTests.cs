using Microsoft.Data.Sqlite;
using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class SqliteDestinationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PeopleWorks.DBFSync.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreatesDatabaseAndReportsExistingFile()
    {
        string path = DatabasePath();
        var factory = new DestinationFactory();
        ConnectionProfile profile = Profile(path);

        DatabaseCreationResult created = await factory.EnsureDatabaseAsync(
            profile,
            string.Empty,
            CancellationToken.None);
        DatabaseCreationResult existing = await factory.EnsureDatabaseAsync(
            profile,
            string.Empty,
            CancellationToken.None);

        Assert.Equal(DatabaseCreationResult.Created, created);
        Assert.Equal(DatabaseCreationResult.AlreadyExists, existing);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MigratesAndSynchronizesChangedInsertedAndDeletedRows()
    {
        string path = DatabasePath();
        DbfTableSchema schema = Schema(
            TextColumn("name", 40),
            DecimalColumn("amount", 28, 8));
        decimal exactAmount = 12345678901234567890.12345678m;

        var initialRows = new[]
        {
            Row(schema, 1, "Original", 10.25m),
            Row(schema, 2, "Delete me", 20m)
        };
        (long Inserted, long Updated, long Deleted) migrated =
            await TransferAsync(path, schema, initialRows, TransferMode.Migrate);

        var synchronizedRows = new[]
        {
            Row(schema, 1, "Changed", exactAmount),
            Row(schema, 3, "New", 30.5m)
        };
        (long Inserted, long Updated, long Deleted) synchronized =
            await TransferAsync(
                path,
                schema,
                synchronizedRows,
                TransferMode.Synchronize);

        Assert.Equal((2L, 0L, 0L), migrated);
        Assert.Equal((1L, 1L, 1L), synchronized);

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(
            """
            SELECT "_dbfsync_recno", "name", "amount", typeof("amount")
            FROM "orders"
            ORDER BY "_dbfsync_recno";
            """,
            connection);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("Changed", reader.GetString(1));
        Assert.Equal(
            exactAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(2));
        Assert.Equal("text", reader.GetString(3));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task AppliesSchemaChangesAndDataInOneTransaction()
    {
        string path = DatabasePath();
        DbfTableSchema original = Schema(
            TextColumn("name", 40),
            TextColumn("legacy", 20));
        await TransferAsync(
            path,
            original,
            [Row(original, 1, "Before", "keep")],
            TransferMode.Migrate);

        DbfTableSchema changed = Schema(
            TextColumn("name", 40),
            TextColumn("added", 60));
        DbfRow[] changedRows = [Row(changed, 1, "After", "new value")];

        await Assert.ThrowsAsync<InvalidDataException>(() => TransferAsync(
            path,
            changed,
            changedRows,
            TransferMode.Synchronize,
            syncSchema: true));

        Assert.Equal(
            ["_dbfsync_recno", "_dbfsync_hash", "name", "legacy", "_dbfsync_at"],
            await ReadColumnNamesAsync(path));
        Assert.Equal("Before", await ReadNameAsync(path));

        await TransferAsync(
            path,
            changed,
            changedRows,
            TransferMode.Synchronize,
            syncSchema: true,
            allowDropColumns: true);

        Assert.Equal(
            ["_dbfsync_recno", "_dbfsync_hash", "name", "_dbfsync_at", "added"],
            await ReadColumnNamesAsync(path));
        Assert.Equal("After", await ReadNameAsync(path));
    }

    [Fact]
    public async Task RejectsConcurrentWritersForTheSameDatabase()
    {
        string path = DatabasePath();
        DbfTableSchema schema = Schema(TextColumn("name", 20));
        var factory = new DestinationFactory();
        ConnectionProfile profile = Profile(path);
        await factory.EnsureDatabaseAsync(profile, string.Empty, CancellationToken.None);

        await using ITableDestination first = factory.Create(profile, string.Empty);
        await first.PrepareAsync(
            schema,
            recreate: false,
            new SchemaSyncOptions(false, false),
            CancellationToken.None);
        await using ITableDestination second = factory.Create(profile, string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => second.PrepareAsync(
            schema,
            recreate: false,
            new SchemaSyncOptions(false, false),
            CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static async Task<(long Inserted, long Updated, long Deleted)> TransferAsync(
        string path,
        DbfTableSchema schema,
        IReadOnlyList<DbfRow> rows,
        TransferMode mode,
        bool syncSchema = false,
        bool allowDropColumns = false)
    {
        var factory = new DestinationFactory();
        ConnectionProfile profile = Profile(path);
        await factory.EnsureDatabaseAsync(profile, string.Empty, CancellationToken.None);
        await using ITableDestination destination = factory.Create(profile, string.Empty);
        await destination.PrepareAsync(
            schema,
            recreate: false,
            new SchemaSyncOptions(syncSchema, allowDropColumns),
            CancellationToken.None);
        await destination.WriteBatchAsync(rows, CancellationToken.None);
        return await destination.CompleteAsync(mode, CancellationToken.None);
    }

    private string DatabasePath() => Path.Combine(_directory, "destination.db");

    private static ConnectionProfile Profile(string path) =>
        new()
        {
            Name = "sqlite-test",
            Engine = DatabaseEngine.SQLite,
            Server = string.Empty,
            Database = path,
            Schema = "main",
            SecretScope = SecretScope.CurrentUser
        };

    private static DbfTableSchema Schema(params DbfColumn[] columns) =>
        new("orders", "orders", "orders.dbf", columns);

    private static DbfColumn TextColumn(string name, int length) =>
        new(name, name, DbfColumnKind.Text, length, 0, 0, typeof(string));

    private static DbfColumn DecimalColumn(string name, int precision, int scale) =>
        new(name, name, DbfColumnKind.Decimal, precision, precision, scale, typeof(decimal));

    private static DbfRow Row(
        DbfTableSchema schema,
        long recordNumber,
        params object?[] values)
    {
        using var hasher = new RowHasher();
        return new DbfRow(recordNumber, hasher.Compute(schema.Columns, values), values);
    }

    private static async Task<string[]> ReadColumnNamesAsync(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(
            """PRAGMA table_info("orders");""",
            connection);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));
        return names.ToArray();
    }

    private static async Task<string> ReadNameAsync(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(
            """SELECT "name" FROM "orders" WHERE "_dbfsync_recno" = 1;""",
            connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
