using System.Reflection;
using PeopleWorks.DBFSync;
using Spectre.Console;

namespace PeopleWorks.DBFSync.Cli;

internal sealed class CliApplication
{
    private readonly AppPaths _paths;
    private readonly ProfileStore _profiles;
    private readonly ISecretProtector _secrets;
    private readonly DestinationFactory _destinations;
    private readonly DbfSource _dbfSource;

    public CliApplication(AppPaths paths)
    {
        _paths = paths;
        _profiles = new ProfileStore(paths.ProfilesFile);
        _secrets = new DpapiSecretProtector();
        _destinations = new DestinationFactory();
        _dbfSource = new DbfSource();
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length > 0 && args[0] is "--version" or "-v" or "version")
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        CliPresentation.ShowBanner(GetVersion(), _profiles.List());
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }
        if (args[0] is "--help" or "-h" or "help" or "ayuda")
        {
            PrintHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "profile" or "perfil" => await RunProfileAsync(args[1..], cancellationToken),
            "database" or "db" or "base" => await RunDatabaseAsync(
                args[1..],
                cancellationToken),
            "migrate" or "migrar" => await RunTransferAsync(
                args[1..],
                TransferMode.Migrate,
                cancellationToken),
            "sync" or "synchronize" or "sincronizar" => await RunTransferAsync(
                args[1..],
                TransferMode.Synchronize,
                cancellationToken),
            "inspect" or "inspeccionar" => RunInspect(args[1..]),
            "sample" or "samples" or "example" or "examples" or
            "ejemplo" or "ejemplos" or "--sample" or "--example" => ShowSamples(),
            _ => throw new ArgumentException(L10n.T("UnknownCommand", args[0]))
        };
    }

    private async Task<int> RunProfileAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new ArgumentException(L10n.T("MissingProfileAction"));

        return args[0].ToLowerInvariant() switch
        {
            "set" or "guardar" => await SetProfileAsync(args[1..], cancellationToken),
            "list" or "listar" => ListProfiles(),
            "show" or "mostrar" => ShowProfile(args[1..]),
            "test" or "probar" => await TestProfileAsync(args[1..], cancellationToken),
            "remove" or "delete" or "eliminar" => RemoveProfile(args[1..]),
            _ => throw new ArgumentException(L10n.T("UnknownProfileAction", args[0]))
        };
    }

    private async Task<int> SetProfileAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        CliArguments options = CliArguments.Parse(args);
        options.EnsureOnly(
            "engine",
            "server",
            "port",
            "database",
            "schema",
            "user",
            "integrated",
            "no-encrypt",
            "trust-server-certificate",
            "scope",
            "password-stdin",
            "file",
            "create-database");
        if (options.Positionals.Count != 1)
            throw new ArgumentException(L10n.T("ProfileSetUsage"));

        string name = options.Positionals[0];
        DatabaseEngine engine = ParseEngine(options.GetRequired("engine"));
        bool sqlite = engine == DatabaseEngine.SQLite;
        bool integrated = options.GetFlag("integrated");
        if (sqlite &&
            (options.Get("server") is not null ||
             options.Get("port") is not null ||
             options.Get("schema") is not null ||
             options.Get("user") is not null ||
             options.Get("scope") is not null ||
             options.Get("integrated") is not null ||
             options.Get("no-encrypt") is not null ||
             options.Get("trust-server-certificate") is not null ||
             options.Get("password-stdin") is not null))
            throw new ArgumentException(L10n.T("SqliteNoCredentials"));
        if (!sqlite && options.Get("file") is not null)
            throw new ArgumentException(L10n.T("FileSqliteOnly"));
        int? port = sqlite ? null : ParseOptionalPort(options.Get("port"));
        string schema = sqlite
            ? "main"
            : options.Get("schema") ??
              (engine == DatabaseEngine.SqlServer ? "dbo" : "public");
        SecretScope scope = sqlite
            ? SecretScope.CurrentUser
            : ParseScope(options.Get("scope") ?? "user");
        string database = sqlite
            ? options.Get("file") ?? options.Get("database") ??
              throw new ArgumentException(L10n.T("SqliteFileRequired"))
            : options.GetRequired("database");

        string plainPassword = string.Empty;
        string? protectedPassword = null;
        if (!sqlite && !integrated)
        {
            plainPassword = options.GetFlag("password-stdin")
                ? ReadPasswordFromStandardInput()
                : PasswordPrompt.Read(L10n.T("PasswordPrompt"));
            protectedPassword = _secrets.Protect(plainPassword, scope);
        }

        ConnectionProfile? previous = _profiles.List().SingleOrDefault(profile =>
            profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var profile = new ConnectionProfile
        {
            Name = name,
            Engine = engine,
            Server = sqlite ? string.Empty : options.GetRequired("server"),
            Port = port,
            Database = sqlite ? Path.GetFullPath(database) : database,
            Schema = sqlite ? "main" : SqlIdentifier.Normalize(schema, "IdentifierSchema"),
            Username = sqlite || integrated ? null : options.GetRequired("user"),
            IntegratedSecurity = integrated,
            Encrypt = !sqlite && !options.GetFlag("no-encrypt"),
            TrustServerCertificate = !sqlite && options.GetFlag("trust-server-certificate"),
            SecretScope = scope,
            ProtectedPassword = protectedPassword,
            CreatedUtc = previous?.CreatedUtc ?? DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        profile.Validate();

        if (sqlite || options.GetFlag("create-database"))
        {
            DatabaseCreationResult creation = await _destinations.EnsureDatabaseAsync(
                profile,
                plainPassword,
                cancellationToken);
            CliPresentation.Success(L10n.T(
                creation == DatabaseCreationResult.Created
                    ? "DatabaseCreated"
                    : "DatabaseAlreadyExists",
                profile.Database));
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync(
                Markup.Escape(L10n.T(
                    "TestingConnection",
                    sqlite ? profile.Database : profile.Server)),
                async _ => await _destinations.TestConnectionAsync(
                    profile,
                    plainPassword,
                    cancellationToken));
        _profiles.Upsert(profile);
        CliPresentation.Success(L10n.T("ProfileSaved", profile.Name, _profiles.Path));
        CliPresentation.ShowProfileContext(profile, L10n.T("VerifiedConnectionTitle"));
        if (!sqlite && scope == SecretScope.CurrentUser)
            CliPresentation.Info(L10n.T("TaskSameUser"));
        else if (!sqlite)
            CliPresentation.Warning(L10n.T("MachineSecret"));
        return 0;
    }

    private async Task<int> RunDatabaseAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 ||
            args[0].ToLowerInvariant() is not
                ("create" or "ensure" or "crear" or "asegurar"))
            throw new ArgumentException(L10n.T("DatabaseActionUsage"));

        CliArguments options = CliArguments.Parse(args[1..]);
        options.EnsureOnly("profile");
        if (options.Positionals.Count > 0)
            throw new ArgumentException(L10n.T("DatabaseActionUsage"));

        ConnectionProfile profile =
            _profiles.GetRequired(options.GetRequired("profile"));
        string password = ResolvePassword(profile);
        DatabaseCreationResult result = await _destinations.EnsureDatabaseAsync(
            profile,
            password,
            cancellationToken);
        CliPresentation.Success(L10n.T(
            result == DatabaseCreationResult.Created
                ? "DatabaseCreated"
                : "DatabaseAlreadyExists",
            profile.Database));
        return 0;
    }

    private int ListProfiles()
    {
        IReadOnlyList<ConnectionProfile> profiles = _profiles.List();
        if (profiles.Count == 0)
        {
            CliPresentation.Warning(L10n.T("NoProfilesAt", _profiles.Path));
            return 0;
        }

        CliPresentation.ShowProfileList(profiles);
        return 0;
    }

    private int ShowProfile(string[] args)
    {
        string name = RequireSinglePositional(args, "profile show");
        ConnectionProfile profile = _profiles.GetRequired(name);
        CliPresentation.ShowProfileDetails(profile);
        return 0;
    }

    private async Task<int> TestProfileAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        string name = RequireSinglePositional(args, "profile test");
        ConnectionProfile profile = _profiles.GetRequired(name);
        string password = ResolvePassword(profile);
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync(
                Markup.Escape(L10n.T("VerifyingProfile", profile.Name)),
                async _ => await _destinations.TestConnectionAsync(
                    profile,
                    password,
                    cancellationToken));
        CliPresentation.Success(L10n.T("ProfileConnectionOk", profile.Name));
        CliPresentation.ShowProfileContext(profile, L10n.T("VerifiedConnectionTitle"));
        return 0;
    }

    private int RemoveProfile(string[] args)
    {
        string name = RequireSinglePositional(args, "profile remove");
        if (!_profiles.Remove(name))
            throw new KeyNotFoundException(L10n.T("ProfileDoesNotExist", name));
        CliPresentation.Success(L10n.T("ProfileRemoved", name));
        return 0;
    }

    private async Task<int> RunTransferAsync(
        string[] args,
        TransferMode mode,
        CancellationToken cancellationToken)
    {
        CliArguments options = CliArguments.Parse(args);
        options.EnsureOnly(
            "profile",
            "source",
            "tables",
            "table",
            "exclude",
            "all",
            "batch-size",
            "recreate",
            "sync-schema",
            "allow-drop-columns");
        if (options.Positionals.Count > 0)
            throw new ArgumentException(L10n.T("TransferNoPositionals"));

        string profileName = options.GetRequired("profile");
        ConnectionProfile profile = _profiles.GetRequired(profileName);
        string password = ResolvePassword(profile);
        IReadOnlyList<string> tables = options.GetMany("tables")
            .Concat(options.GetMany("table"))
            .ToArray();
        IReadOnlyList<string> exclusions = options.GetMany("exclude").ToArray();
        var request = new TransferRequest(
            options.GetRequired("source"),
            tables,
            exclusions,
            options.GetFlag("all"),
            mode,
            options.GetFlag("recreate"),
            options.GetFlag("sync-schema"),
            options.GetFlag("allow-drop-columns"),
            options.GetInt("batch-size", 5_000));

        string operation = mode == TransferMode.Migrate ? "migrate" : "sync";
        CliPresentation.ShowProfileContext(profile);
        using var reporter = new RunReporter(_paths.LogsDirectory, operation, profile.Name);
        reporter.Info(L10n.T(
            "TransferStarted",
            operation,
            profile.Name,
            profile.Engine,
            Path.GetFullPath(request.SourceDirectory)));
        try
        {
            var service = new SyncService(_dbfSource, _destinations);
            IReadOnlyList<TableTransferResult> results = await service.ExecuteAsync(
                profile,
                password,
                request,
                reporter.Info,
                cancellationToken);
            reporter.Success(L10n.T(
                "TransferTotal",
                results.Count,
                results.Sum(result => result.RowsRead),
                results.Sum(result => result.Inserted),
                results.Sum(result => result.Updated),
                results.Sum(result => result.Deleted)));
            reporter.Success(L10n.T("LogLocation", reporter.Path));
            return 0;
        }
        catch (Exception ex)
        {
            reporter.Record($"ERROR {ex}");
            throw;
        }
    }

    private int RunInspect(string[] args)
    {
        CliArguments options = CliArguments.Parse(args);
        options.EnsureOnly("source", "tables", "table", "exclude", "all");
        if (options.Positionals.Count > 0)
            throw new ArgumentException(L10n.T("InspectNoPositionals"));

        string source = options.GetRequired("source");
        IReadOnlyList<string> requested = options.GetMany("tables")
            .Concat(options.GetMany("table"))
            .ToArray();
        IReadOnlyList<string> exclusions = options.GetMany("exclude").ToArray();
        IReadOnlyList<string> tables = _dbfSource.ResolveTables(
            source,
            requested,
            exclusions,
            options.GetFlag("all"));
        CliPresentation.Info(L10n.T(
            "SelectedDbfs",
            tables.Count,
            string.Join(", ", tables)));
        foreach (string tableName in tables)
        {
            DbfTableSchema table = _dbfSource.ReadSchema(source, tableName);
            long physical = _dbfSource.CountRows(source, tableName, excludeDeleted: false);
            long active = _dbfSource.CountRows(source, tableName, excludeDeleted: true);
            DbfRow? sample = _dbfSource.ReadRows(source, table, CancellationToken.None)
                .Take(1)
                .SingleOrDefault();
            CliPresentation.ShowInspection(table, physical, active, sample);
        }
        return 0;
    }

    private string ResolvePassword(ConnectionProfile profile) =>
        profile.Engine == DatabaseEngine.SQLite || profile.IntegratedSecurity
            ? string.Empty
            : _secrets.Unprotect(profile.ProtectedPassword
                ?? throw new InvalidDataException(
                    L10n.T("ProfilePasswordMissing", profile.Name)));

    private static string ReadPasswordFromStandardInput()
    {
        string? password = Console.In.ReadLine();
        return !string.IsNullOrEmpty(password)
            ? password
            : throw new ArgumentException(L10n.T("StdinPasswordMissing"));
    }

    private static string RequireSinglePositional(string[] args, string command)
    {
        CliArguments parsed = CliArguments.Parse(args);
        parsed.EnsureOnly();
        return parsed.Positionals.Count == 1
            ? parsed.Positionals[0]
            : throw new ArgumentException(L10n.T("CommandUsage", command));
    }

    private static DatabaseEngine ParseEngine(string value) =>
        value.ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "pg" => DatabaseEngine.PostgreSql,
            "sqlserver" or "mssql" => DatabaseEngine.SqlServer,
            "sqlite" or "sqlite3" => DatabaseEngine.SQLite,
            _ => throw new ArgumentException(L10n.T("EngineInvalid"))
        };

    private static SecretScope ParseScope(string value) =>
        value.ToLowerInvariant() switch
        {
            "user" or "usuario" => SecretScope.CurrentUser,
            "machine" or "maquina" or "máquina" => SecretScope.LocalMachine,
            _ => throw new ArgumentException(L10n.T("ScopeInvalid"))
        };

    private static int? ParseOptionalPort(string? value)
    {
        if (value is null)
            return null;
        if (!int.TryParse(value, out int port))
            throw new ArgumentException(L10n.T("PortInvalid"));
        return port;
    }

    private static void PrintHelp()
    {
        CliPresentation.Section(L10n.T("HelpProfilesTitle"));
        AnsiConsole.WriteLine(L10n.T("HelpProfileSet"));
        AnsiConsole.WriteLine(L10n.T("HelpProfileOptions1"));
        AnsiConsole.WriteLine(L10n.T("HelpProfileOptions2"));
        AnsiConsole.WriteLine(L10n.T("HelpProfileSqlite"));
        AnsiConsole.WriteLine(L10n.T("HelpProfileManage"));
        AnsiConsole.WriteLine(L10n.T("HelpDatabaseCreate"));
        Console.WriteLine();
        CliPresentation.Section(L10n.T("HelpSelectionTitle"));
        AnsiConsole.WriteLine(L10n.T("HelpInspect"));
        AnsiConsole.WriteLine(L10n.T("HelpMigrate"));
        AnsiConsole.WriteLine(L10n.T("HelpSync"));
        AnsiConsole.WriteLine(L10n.T("HelpSchemaSync"));
        AnsiConsole.WriteLine(L10n.T("HelpRecreate"));
        Console.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(L10n.T("HelpPatterns"))}[/]");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(L10n.T("HelpPatternExample"))}[/]");
        Console.WriteLine();
        CliPresentation.Info(L10n.T("HelpPassword"));
        CliPresentation.Info(L10n.T("HelpSsl"));
        CliPresentation.Info(L10n.T("HelpTask"));
        CliPresentation.Info(L10n.T("HelpLanguage"));
        CliPresentation.Info(L10n.T("HelpSamples"));
    }

    private static int ShowSamples()
    {
        CliPresentation.ShowSamples();
        return 0;
    }

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
