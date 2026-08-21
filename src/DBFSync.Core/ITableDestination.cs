namespace PeopleWorks.DBFSync;

public interface ITableDestination : IAsyncDisposable
{
    IReadOnlyList<string> SchemaChanges { get; }

    Task PrepareAsync(
        DbfTableSchema table,
        bool recreate,
        SchemaSyncOptions schemaSync,
        CancellationToken cancellationToken);

    Task WriteBatchAsync(
        IReadOnlyList<DbfRow> rows,
        CancellationToken cancellationToken);

    Task<(long Inserted, long Updated, long Deleted)> CompleteAsync(
        TransferMode mode,
        CancellationToken cancellationToken);
}
