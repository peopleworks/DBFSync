using Spectre.Console;

namespace PeopleWorks.DBFSync.Cli;

internal static class CliPresentation
{
    public static void ShowBanner(
        string version,
        IReadOnlyList<ConnectionProfile> profiles)
    {
        if (AnsiConsole.Profile.Width >= 110)
        {
            AnsiConsole.Write(
                new FigletText("PeopleWorks")
                    .Centered()
                    .Color(Color.Blue));
            AnsiConsole.Write(
                new FigletText("DBFSync")
                    .Centered()
                    .Color(Color.Cyan1));
        }
        else
        {
            AnsiConsole.Write(
                new Panel(
                    Align.Center(
                        new Markup(
                            "[bold blue]P E O P L E W O R K S[/]\n" +
                            "[bold cyan1]D B F S Y N C[/]")))
                {
                    Border = BoxBorder.Rounded,
                    Padding = new Padding(2, 1)
                });
        }

        AnsiConsole.MarkupLine(
            $"[dim]{Escape(L10n.T("BannerSubtitle", version))}[/]");
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{Escape(L10n.T("NoProfilesSaved"))}[/]");
        }
        else
        {
            string names = string.Join(
                ", ",
                profiles.Take(5).Select(profile => profile.Name));
            string suffix = profiles.Count > 5 ? ", ..." : string.Empty;
            AnsiConsole.MarkupLine(
                $"[green]●[/] [dim]{Escape(L10n.T("SavedProfilesCount", profiles.Count, names, suffix))}[/]");
        }
        AnsiConsole.WriteLine();
    }

    public static void ShowProfileContext(
        ConnectionProfile profile,
        string? title = null)
    {
        string endpoint = profile.Engine == DatabaseEngine.SQLite
            ? Path.GetFullPath(profile.Database)
            : profile.Port.HasValue
                ? $"{profile.Server}:{profile.Port.Value}"
                : profile.Server;
        string identity = profile.Engine == DatabaseEngine.SQLite
            ? L10n.T("LabelNoCredentials")
            : profile.IntegratedSecurity
                ? $"{Environment.UserDomainName}\\{Environment.UserName} (Windows)"
                : profile.Username ?? L10n.T("LabelNoUser");

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders();
        table.AddColumn(string.Empty);
        table.AddColumn(string.Empty);
        table.AddRow($"[dim]{Escape(L10n.T("LabelProfile"))}[/]", $"[bold cyan1]{Escape(profile.Name)}[/]");
        table.AddRow($"[dim]{Escape(L10n.T("LabelEngine"))}[/]", Escape(FormatEngine(profile.Engine)));
        table.AddRow(
            $"[dim]{Escape(profile.Engine == DatabaseEngine.SQLite ? L10n.T("LabelFile") : L10n.T("LabelServer"))}[/]",
            Escape(endpoint));
        if (profile.Engine != DatabaseEngine.SQLite)
            table.AddRow($"[dim]{Escape(L10n.T("LabelDatabaseSchema"))}[/]",
                $"{Escape(profile.Database)} / {Escape(profile.Schema)}");
        table.AddRow($"[dim]{Escape(L10n.T("LabelConnectedAs"))}[/]", Escape(identity));
        if (profile.Engine != DatabaseEngine.SQLite)
            table.AddRow($"[dim]{Escape(L10n.T("LabelDpapiCredential"))}[/]",
                Escape(profile.SecretScope == SecretScope.CurrentUser
                    ? L10n.T("LabelUser")
                    : L10n.T("LabelMachine")));

        AnsiConsole.Write(
            new Panel(table)
            {
                Header = new PanelHeader(
                    $"[bold green]{Escape(title ?? L10n.T("ActiveProfileTitle"))}[/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 0)
            });
    }

    public static void ShowProfileList(IReadOnlyList<ConnectionProfile> profiles)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold cyan1]{Escape(L10n.T("LabelSavedProfiles"))}[/]");
        table.AddColumn($"[bold]{Escape(L10n.T("LabelName"))}[/]");
        table.AddColumn($"[bold]{Escape(L10n.T("LabelEngine"))}[/]");
        table.AddColumn($"[bold]{Escape(L10n.T("LabelServerOrFile"))}[/]");
        table.AddColumn($"[bold]{Escape(L10n.T("LabelDatabase"))}[/]");
        table.AddColumn($"[bold]{Escape(L10n.T("LabelSchema"))}[/]");
        table.AddColumn($"[bold]{Escape(L10n.T("LabelUser"))}[/]");
        table.AddColumn("[bold]DPAPI[/]");

        foreach (ConnectionProfile profile in profiles)
        {
            string endpoint = profile.Engine == DatabaseEngine.SQLite
                ? Path.GetFullPath(profile.Database)
                : profile.Port.HasValue
                    ? $"{profile.Server}:{profile.Port.Value}"
                    : profile.Server;
            table.AddRow(
                $"[cyan1]{Escape(profile.Name)}[/]",
                Escape(FormatEngine(profile.Engine)),
                Escape(endpoint),
                Escape(profile.Engine == DatabaseEngine.SQLite
                    ? Path.GetFileName(profile.Database)
                    : profile.Database),
                Escape(profile.Engine == DatabaseEngine.SQLite ? "-" : profile.Schema),
                Escape(profile.Engine == DatabaseEngine.SQLite
                    ? L10n.T("LabelNoCredentials")
                    : profile.IntegratedSecurity ? L10n.T("LabelWindows") : profile.Username ?? "-"),
                Escape(profile.Engine == DatabaseEngine.SQLite
                    ? "-"
                    : profile.SecretScope == SecretScope.CurrentUser
                        ? L10n.T("LabelUser")
                        : L10n.T("LabelMachine")));
        }
        AnsiConsole.Write(table);
    }

    public static void ShowProfileDetails(ConnectionProfile profile)
    {
        ShowProfileContext(profile, L10n.T("ConnectionProfileTitle"));
        if (profile.Engine == DatabaseEngine.SQLite)
        {
            AnsiConsole.MarkupLine(
                $"[dim]{Escape(L10n.T("LabelUpdated"))}[/] " +
                Escape(profile.UpdatedUtc.ToString("O")));
            return;
        }

        AnsiConsole.MarkupLine(
            $"[dim]{Escape(L10n.T("LabelEncryptedTransport"))}[/] " +
            $"{(profile.Encrypt ? $"[green]{Escape(L10n.T("LabelYes"))}[/]" : $"[yellow]{Escape(L10n.T("LabelNo"))}[/]")}   " +
            $"[dim]{Escape(L10n.T("LabelCertificate"))}[/] " +
            $"{(profile.TrustServerCertificate ? $"[yellow]{Escape(L10n.T("LabelTrustServer"))}[/]" : $"[green]{Escape(L10n.T("LabelValidate"))}[/]")}   " +
            $"[dim]{Escape(L10n.T("LabelUpdated"))}[/] {Escape(profile.UpdatedUtc.ToString("O"))}");
    }

    public static void ShowInspection(
        DbfTableSchema table,
        long physicalRows,
        long activeRows,
        DbfRow? sample)
    {
        var summary = new Table()
            .Border(TableBorder.None)
            .HideHeaders();
        summary.AddColumn(string.Empty);
        summary.AddColumn(string.Empty);
        summary.AddRow($"[dim]{Escape(L10n.T("LabelFile"))}[/]", Escape(table.FilePath));
        summary.AddRow($"[dim]{Escape(L10n.T("LabelTargetTable"))}[/]", $"[cyan1]{Escape(table.TargetName)}[/]");
        summary.AddRow($"[dim]{Escape(L10n.T("LabelColumns"))}[/]", table.Columns.Count.ToString());
        summary.AddRow($"[dim]{Escape(L10n.T("LabelActiveRows"))}[/]", $"[green]{activeRows:N0}[/]");
        summary.AddRow($"[dim]{Escape(L10n.T("LabelDeletedRows"))}[/]", $"[yellow]{physicalRows - activeRows:N0}[/]");
        if (sample is not null)
        {
            summary.AddRow(
                $"[dim]{Escape(L10n.T("LabelSample"))}[/]",
                $"RECNO={sample.RecordNumber} · SHA-256={Convert.ToHexString(sample.Hash)}");
        }
        AnsiConsole.Write(
            new Panel(summary)
            {
                Header = new PanelHeader($"[bold blue]{Escape(table.SourceName)}[/]"),
                Border = BoxBorder.Rounded
            });

        var columns = new Table().Border(TableBorder.Simple);
        columns.AddColumn($"[bold]{Escape(L10n.T("LabelDbfField"))}[/]");
        columns.AddColumn($"[bold]{Escape(L10n.T("LabelType"))}[/]");
        columns.AddColumn(new TableColumn($"[bold]{Escape(L10n.T("LabelLength"))}[/]").RightAligned());
        columns.AddColumn(new TableColumn($"[bold]{Escape(L10n.T("LabelPrecision"))}[/]").RightAligned());
        columns.AddColumn(new TableColumn($"[bold]{Escape(L10n.T("LabelScale"))}[/]").RightAligned());
        foreach (DbfColumn column in table.Columns)
        {
            columns.AddRow(
                Escape(column.SourceName),
                Escape(L10n.ColumnKind(column.Kind)),
                column.Length.ToString(),
                column.Precision.ToString(),
                column.Scale.ToString());
        }
        AnsiConsole.Write(columns);
        AnsiConsole.WriteLine();
    }

    public static void Info(string message) =>
        AnsiConsole.MarkupLine($"[blue]›[/] {Escape(message)}");

    public static void Success(string message) =>
        AnsiConsole.MarkupLine($"[green]✓[/] {Escape(message)}");

    public static void Warning(string message) =>
        AnsiConsole.MarkupLine($"[yellow]⚠[/] {Escape(message)}");

    public static void Error(string message)
    {
        IAnsiConsole errorConsole = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(Console.Error)
            });
        errorConsole.MarkupLine(
            $"[red]✗ {Escape(L10n.T("ErrorLabel"))}:[/] {Escape(message)}");
    }

    public static void Section(string title) =>
        AnsiConsole.Write(
            new Rule($"[bold cyan1]{Escape(title)}[/]")
            {
                Justification = Justify.Left,
                Style = Style.Parse("blue")
            });

    public static void ShowSamples()
    {
        Section(L10n.T("SamplesTitle"));
        string[] samplePrefixes =
        [
            "SampleSqlite",
            "SamplePostgres",
            "SampleSqlServer",
            "SampleIntegrated",
            "SampleInspect",
            "SampleMigrate",
            "SampleSchemaSync",
            "SampleSchemaDrop",
            "SampleRecreate",
            "SampleSync",
            "SampleAll",
            "SampleDatabase",
            "SampleLanguage"
        ];
        foreach (string prefix in samplePrefixes)
        {
            AnsiConsole.MarkupLine(
                $"\n[bold blue]{Escape(L10n.T(prefix + "Title"))}[/]");
            AnsiConsole.MarkupLine(
                $"[dim]{Escape(L10n.T(prefix + "Description"))}[/]");
            AnsiConsole.Write(
                new Panel(Escape(L10n.T(prefix + "Command")))
                {
                    Border = BoxBorder.Rounded,
                    Padding = new Padding(1, 0)
                });
        }
    }

    private static string FormatEngine(DatabaseEngine engine) =>
        engine switch
        {
            DatabaseEngine.PostgreSql => "PostgreSQL",
            DatabaseEngine.SqlServer => "SQL Server",
            DatabaseEngine.SQLite => "SQLite",
            _ => engine.ToString()
        };

    private static string Escape(string? value) =>
        Markup.Escape(value ?? string.Empty);
}
