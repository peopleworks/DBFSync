using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace PeopleWorks.DBFSync;

public enum DatabaseEngine
{
    PostgreSql,
    SqlServer,
    SQLite
}

public enum SecretScope
{
    CurrentUser,
    LocalMachine
}

public enum TransferMode
{
    Migrate,
    Synchronize
}

public enum DbfColumnKind
{
    Text,
    Decimal,
    Integer,
    Double,
    Boolean,
    Date,
    DateTime,
    Binary
}

public sealed class ConnectionProfile
{
    public required string Name { get; init; }
    public required DatabaseEngine Engine { get; init; }
    public required string Server { get; init; }
    public int? Port { get; init; }
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public string? Username { get; init; }
    public bool IntegratedSecurity { get; init; }
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public required SecretScope SecretScope { get; init; }
    public string? ProtectedPassword { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) ||
            Name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException(L10n.T("ProfileNameInvalid"));
        if (Engine != DatabaseEngine.SQLite && string.IsNullOrWhiteSpace(Server))
            throw new ArgumentException(L10n.T("ProfileServerRequired"));
        if (string.IsNullOrWhiteSpace(Database))
            throw new ArgumentException(L10n.T("ProfileDatabaseRequired"));
        if (Engine != DatabaseEngine.SQLite)
            SqlIdentifier.Validate(Schema, "IdentifierSchema");
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), L10n.T("ProfilePortRange"));
        if (IntegratedSecurity && Engine != DatabaseEngine.SqlServer)
            throw new ArgumentException(L10n.T("IntegratedSqlServerOnly"));
        if (Engine != DatabaseEngine.SQLite &&
            !IntegratedSecurity &&
            string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException(L10n.T("ProfileUserRequired"));
        if (Engine != DatabaseEngine.SQLite &&
            !IntegratedSecurity &&
            string.IsNullOrWhiteSpace(ProtectedPassword))
            throw new ArgumentException(L10n.T("ProtectedPasswordRequired"));
    }

    public string BuildConnectionString(string password)
    {
        Validate();
        if (Engine == DatabaseEngine.SQLite)
        {
            var sqlite = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(Database),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                ForeignKeys = true,
                Pooling = true,
                DefaultTimeout = 30
            };
            return sqlite.ConnectionString;
        }

        if (Engine == DatabaseEngine.SqlServer)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = Port.HasValue ? $"{Server},{Port.Value}" : Server,
                InitialCatalog = Database,
                IntegratedSecurity = IntegratedSecurity,
                Encrypt = Encrypt
                    ? SqlConnectionEncryptOption.Mandatory
                    : SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = TrustServerCertificate,
                ApplicationName = "PeopleWorks DBFSync",
                ConnectTimeout = 15,
                Pooling = true
            };
            if (!IntegratedSecurity)
            {
                builder.UserID = Username;
                builder.Password = password;
            }
            return builder.ConnectionString;
        }

        var npgsql = new NpgsqlConnectionStringBuilder
        {
            Host = Server,
            Port = Port ?? 5432,
            Database = Database,
            Username = Username,
            Password = password,
            ApplicationName = "PeopleWorks DBFSync",
            Timeout = 15,
            CommandTimeout = 0,
            Pooling = true,
            SslMode = !Encrypt
                ? SslMode.Disable
                : TrustServerCertificate ? SslMode.Require : SslMode.VerifyFull
        };
        return npgsql.ConnectionString;
    }
}

public sealed record DbfColumn(
    string SourceName,
    string TargetName,
    DbfColumnKind Kind,
    int Length,
    int Precision,
    int Scale,
    Type ClrType);

public sealed record DbfTableSchema(
    string SourceName,
    string TargetName,
    string FilePath,
    IReadOnlyList<DbfColumn> Columns);

public sealed record DbfRow(long RecordNumber, byte[] Hash, object?[] Values);

public sealed record TransferRequest(
    string SourceDirectory,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> ExcludedTables,
    bool AllTables,
    TransferMode Mode,
    bool Recreate,
    bool SyncSchema,
    bool AllowDropColumns,
    int BatchSize);

public sealed record SchemaSyncOptions(bool Enabled, bool AllowDropColumns);

public enum DatabaseCreationResult
{
    Created,
    AlreadyExists
}

public sealed record TableTransferResult(
    string SourceTable,
    string TargetTable,
    long RowsRead,
    long Inserted,
    long Updated,
    long Deleted,
    TimeSpan Duration);
