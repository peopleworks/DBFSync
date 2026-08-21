using System.Diagnostics;

namespace PeopleWorks.DBFSync;

public sealed class SyncService
{
    private readonly DbfSource _source;
    private readonly DestinationFactory _destinations;

    public SyncService(DbfSource source, DestinationFactory destinations)
    {
        _source = source;
        _destinations = destinations;
    }

    public async Task<IReadOnlyList<TableTransferResult>> ExecuteAsync(
        ConnectionProfile profile,
        string plainPassword,
        TransferRequest request,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (request.BatchSize is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                L10n.T("BatchSizeRange"));
        if (request.Recreate && request.Mode != TransferMode.Migrate)
            throw new ArgumentException(L10n.T("RecreateMigrateOnly"));
        if (request.Recreate && request.SyncSchema)
            throw new ArgumentException(L10n.T("RecreateSchemaSyncExclusive"));
        if (request.AllowDropColumns && !request.SyncSchema)
            throw new ArgumentException(L10n.T("AllowDropRequiresSchemaSync"));

        IReadOnlyList<string> tables = _source.ResolveTables(
            request.SourceDirectory,
            request.Tables,
            request.ExcludedTables,
            request.AllTables);
        string selection = string.Join(", ", tables.Take(12));
        if (tables.Count > 12)
            selection += $", ... (+{tables.Count - 12})";
        progress?.Invoke(L10n.T("SyncSelection", tables.Count, selection));
        var results = new List<TableTransferResult>(tables.Count);

        foreach (string tableName in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            DbfTableSchema table = _source.ReadSchema(request.SourceDirectory, tableName);
            progress?.Invoke(L10n.T(
                "ReadingTable",
                table.SourceName,
                table.Columns.Count,
                table.FilePath));

            await using ITableDestination destination =
                _destinations.Create(profile, plainPassword);
            await destination.PrepareAsync(
                table,
                request.Recreate,
                new SchemaSyncOptions(request.SyncSchema, request.AllowDropColumns),
                cancellationToken);

            long rowsRead = 0;
            var batch = new List<DbfRow>(request.BatchSize);
            foreach (DbfRow row in _source.ReadRows(
                         request.SourceDirectory,
                         table,
                         cancellationToken))
            {
                batch.Add(row);
                rowsRead++;
                if (batch.Count < request.BatchSize)
                    continue;

                await destination.WriteBatchAsync(batch, cancellationToken);
                batch.Clear();
                if (rowsRead % 100_000 == 0)
                    progress?.Invoke(L10n.T(
                        "RowsPrepared",
                        table.SourceName,
                        rowsRead));
            }
            if (batch.Count > 0)
                await destination.WriteBatchAsync(batch, cancellationToken);

            (long inserted, long updated, long deleted) =
                await destination.CompleteAsync(request.Mode, cancellationToken);
            foreach (string schemaChange in destination.SchemaChanges)
                progress?.Invoke(schemaChange);
            stopwatch.Stop();
            var result = new TableTransferResult(
                table.SourceName,
                profile.Engine == DatabaseEngine.SQLite
                    ? table.TargetName
                    : $"{profile.Schema}.{table.TargetName}",
                rowsRead,
                inserted,
                updated,
                deleted,
                stopwatch.Elapsed);
            results.Add(result);
            progress?.Invoke(L10n.T(
                "TableTransferSummary",
                table.SourceName,
                rowsRead,
                inserted,
                updated,
                deleted,
                stopwatch.Elapsed));
        }

        return results;
    }
}
