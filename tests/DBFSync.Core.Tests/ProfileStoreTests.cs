using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public void StoresMultipleNamedProfilesWithoutPlaintextPassword()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "profiles.json");
            var store = new ProfileStore(path);
            var secrets = new DpapiSecretProtector();

            store.Upsert(CreateProfile(
                "central-pg",
                DatabaseEngine.PostgreSql,
                secrets.Protect("super-secret", SecretScope.CurrentUser)));
            store.Upsert(CreateProfile(
                "reportes-sql",
                DatabaseEngine.SqlServer,
                secrets.Protect("another-secret", SecretScope.CurrentUser)));

            Assert.Equal(2, store.List().Count);
            Assert.Equal(DatabaseEngine.PostgreSql, store.GetRequired("CENTRAL-PG").Engine);
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("another-secret", json, StringComparison.Ordinal);
            Assert.Contains("dpapi:u:", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DpapiRoundTripUsesSelectedScope()
    {
        var protector = new DpapiSecretProtector();

        string protectedValue = protector.Protect("clave-prueba", SecretScope.CurrentUser);

        Assert.StartsWith("dpapi:u:", protectedValue, StringComparison.Ordinal);
        Assert.Equal("clave-prueba", protector.Unprotect(protectedValue));
    }

    [Fact]
    public void SQLiteProfileRequiresOnlyAFile()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DBFSync.Tests",
            "local.db");
        var profile = new ConnectionProfile
        {
            Name = "local",
            Engine = DatabaseEngine.SQLite,
            Server = string.Empty,
            Database = path,
            Schema = "main",
            SecretScope = SecretScope.CurrentUser
        };

        profile.Validate();
        string connectionString = profile.BuildConnectionString(string.Empty);

        Assert.Contains(path, connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Null(profile.ProtectedPassword);
        Assert.Null(profile.Username);
    }

    private static ConnectionProfile CreateProfile(
        string name,
        DatabaseEngine engine,
        string password) =>
        new()
        {
            Name = name,
            Engine = engine,
            Server = "localhost",
            Port = engine == DatabaseEngine.PostgreSql ? 5432 : 1433,
            Database = "erp",
            Schema = engine == DatabaseEngine.PostgreSql ? "public" : "dbo",
            Username = "dbfsync",
            IntegratedSecurity = false,
            Encrypt = true,
            TrustServerCertificate = true,
            SecretScope = SecretScope.CurrentUser,
            ProtectedPassword = password
        };

    private static string CreateTemporaryDirectory()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DBFSync.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
