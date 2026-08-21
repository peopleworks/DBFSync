using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class SyncServiceValidationTests
{
    [Fact]
    public async Task RejectsDropColumnsWithoutSchemaSynchronization()
    {
        var service = new SyncService(new DbfSource(), new DestinationFactory());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteAsync(
                SQLiteProfile(),
                string.Empty,
                Request(syncSchema: false, allowDropColumns: true),
                progress: null,
                CancellationToken.None));

        Assert.Contains("--sync-schema", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsRecreateTogetherWithSchemaSynchronization()
    {
        var service = new SyncService(new DbfSource(), new DestinationFactory());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteAsync(
                SQLiteProfile(),
                string.Empty,
                Request(syncSchema: true, allowDropColumns: false, recreate: true),
                progress: null,
                CancellationToken.None));

        Assert.Contains("--recreate", error.Message, StringComparison.Ordinal);
    }

    private static ConnectionProfile SQLiteProfile() =>
        new()
        {
            Name = "local",
            Engine = DatabaseEngine.SQLite,
            Server = string.Empty,
            Database = "unused.db",
            Schema = "main",
            SecretScope = SecretScope.CurrentUser
        };

    private static TransferRequest Request(
        bool syncSchema,
        bool allowDropColumns,
        bool recreate = false) =>
        new(
            SourceDirectory: "unused",
            Tables: ["orders"],
            ExcludedTables: [],
            AllTables: false,
            Mode: TransferMode.Migrate,
            Recreate: recreate,
            SyncSchema: syncSchema,
            AllowDropColumns: allowDropColumns,
            BatchSize: 100);
}
