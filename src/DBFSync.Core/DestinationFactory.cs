using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace PeopleWorks.DBFSync;

public sealed class DestinationFactory
{
    public async Task TestConnectionAsync(
        ConnectionProfile profile,
        string plainPassword,
        CancellationToken cancellationToken)
    {
        string connectionString = profile.BuildConnectionString(plainPassword);
        if (profile.Engine == DatabaseEngine.SQLite)
        {
            EnsureSqliteDirectory(profile.Database);
            await using var sqlite = new SqliteConnection(connectionString);
            await sqlite.OpenAsync(cancellationToken);
            await using var sqliteCommand = new SqliteCommand("SELECT 1;", sqlite);
            _ = await sqliteCommand.ExecuteScalarAsync(cancellationToken);
            return;
        }

        if (profile.Engine == DatabaseEngine.SqlServer)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1;", connection);
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return;
        }

        await using var npgsql = new NpgsqlConnection(connectionString);
        await npgsql.OpenAsync(cancellationToken);
        await using var npgsqlCommand = new NpgsqlCommand("SELECT 1;", npgsql);
        _ = await npgsqlCommand.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<DatabaseCreationResult> EnsureDatabaseAsync(
        ConnectionProfile profile,
        string plainPassword,
        CancellationToken cancellationToken)
    {
        profile.Validate();
        if (profile.Engine == DatabaseEngine.SQLite)
        {
            string path = Path.GetFullPath(profile.Database);
            bool existed = File.Exists(path);
            EnsureSqliteDirectory(path);
            await using var connection =
                new SqliteConnection(profile.BuildConnectionString(plainPassword));
            await connection.OpenAsync(cancellationToken);
            return existed
                ? DatabaseCreationResult.AlreadyExists
                : DatabaseCreationResult.Created;
        }

        if (profile.Engine == DatabaseEngine.SqlServer)
        {
            var builder = new SqlConnectionStringBuilder(
                profile.BuildConnectionString(plainPassword))
            {
                InitialCatalog = "master"
            };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var exists = new SqlCommand(
                "SELECT CASE WHEN DB_ID(@database) IS NULL THEN 0 ELSE 1 END;",
                connection);
            exists.Parameters.AddWithValue("@database", profile.Database);
            bool found = Convert.ToInt32(
                await exists.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (found)
                return DatabaseCreationResult.AlreadyExists;

            await using var create = new SqlCommand(
                $"CREATE DATABASE {SqlIdentifier.SqlServer(profile.Database)};",
                connection)
            {
                CommandTimeout = 0
            };
            await create.ExecuteNonQueryAsync(cancellationToken);
            return DatabaseCreationResult.Created;
        }

        var npgsqlBuilder = new NpgsqlConnectionStringBuilder(
            profile.BuildConnectionString(plainPassword))
        {
            Database = "postgres"
        };
        await using var npgsql = new NpgsqlConnection(npgsqlBuilder.ConnectionString);
        await npgsql.OpenAsync(cancellationToken);
        await using var pgExists = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @database);",
            npgsql);
        pgExists.Parameters.AddWithValue("database", profile.Database);
        bool pgFound = Convert.ToBoolean(
            await pgExists.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (pgFound)
            return DatabaseCreationResult.AlreadyExists;

        await using var pgCreate = new NpgsqlCommand(
            $"CREATE DATABASE {SqlIdentifier.PostgreSql(profile.Database)};",
            npgsql)
        {
            CommandTimeout = 0
        };
        await pgCreate.ExecuteNonQueryAsync(cancellationToken);
        return DatabaseCreationResult.Created;
    }

    public ITableDestination Create(ConnectionProfile profile, string plainPassword) =>
        profile.Engine switch
        {
            DatabaseEngine.SqlServer => new SqlServerDestination(
                profile.BuildConnectionString(plainPassword),
                profile.Schema),
            DatabaseEngine.PostgreSql => new PostgreSqlDestination(
                profile.BuildConnectionString(plainPassword),
                profile.Schema),
            DatabaseEngine.SQLite => new SqliteDestination(
                profile.BuildConnectionString(plainPassword)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.Engine,
                L10n.T("UnsupportedEngine"))
        };

    private static void EnsureSqliteDirectory(string databasePath)
    {
        string path = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}
