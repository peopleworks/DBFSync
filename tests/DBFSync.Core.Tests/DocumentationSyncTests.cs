using System.Text.RegularExpressions;

namespace PeopleWorks.DBFSync.Tests;

/// <summary>
/// The CLI option surface is declared once in code, inside the
/// <c>CliArguments.EnsureOnly(...)</c> call of each command, and then repeated in three
/// documentation surfaces: the Spanish README, the English README and the pocket guide.
/// These tests read all four back and fail when any of them drifts, so adding an option
/// without documenting it (or documenting one that no longer exists) breaks CI instead of
/// shipping silently.
/// </summary>
public sealed class DocumentationSyncTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    public static TheoryData<string, string, string, string> Surfaces =>
        new()
        {
            // command method in CliApplication | docs/index.html table | ES heading | EN heading
            {
                "SetProfileAsync",
                "profile-set",
                "### Opciones de `profile set`",
                "### `profile set` options"
            },
            {
                "RunTransferAsync",
                "transfer",
                "### Opciones de transferencia",
                "### Transfer options"
            },
        };

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void PocketGuideDocumentsExactlyTheOptionsTheCodeAccepts(
        string method,
        string guideTable,
        string spanishHeading,
        string englishHeading)
    {
        _ = spanishHeading;
        _ = englishHeading;

        IReadOnlySet<string> code = OptionsFromCode(method);
        IReadOnlySet<string> guide = OptionsFromHtmlTable(guideTable);

        AssertSameOptions(code, guide, $"docs/index.html [data-options=\"{guideTable}\"]");
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void SpanishReadmeDocumentsExactlyTheOptionsTheCodeAccepts(
        string method,
        string guideTable,
        string spanishHeading,
        string englishHeading)
    {
        _ = guideTable;
        _ = englishHeading;

        IReadOnlySet<string> code = OptionsFromCode(method);
        IReadOnlySet<string> readme = OptionsFromMarkdownTable("README.md", spanishHeading);

        AssertSameOptions(code, readme, $"README.md \"{spanishHeading}\"");
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void EnglishReadmeDocumentsExactlyTheOptionsTheCodeAccepts(
        string method,
        string guideTable,
        string spanishHeading,
        string englishHeading)
    {
        _ = guideTable;
        _ = spanishHeading;

        IReadOnlySet<string> code = OptionsFromCode(method);
        IReadOnlySet<string> readme = OptionsFromMarkdownTable("README.en.md", englishHeading);

        AssertSameOptions(code, readme, $"README.en.md \"{englishHeading}\"");
    }

    [Fact]
    public void EveryCommandSurfaceIsCoveredByThisTest()
    {
        // Guards the guard: a command that starts accepting options must be added to one of
        // the assertions above, otherwise its options could go undocumented unnoticed.
        // A parameterless EnsureOnly() means "this command accepts no options at all" and is
        // deliberately not a documentation surface.
        string[] covered =
        [
            "RunDatabaseAsync",
            "RunInspect",
            "RunTransferAsync",
            "SetProfileAsync",
        ];

        Assert.Equal(covered, MethodsAcceptingOptions());
    }

    [Fact]
    public void DatabaseAndInspectOptionsAreMentionedInEveryDocumentationSurface()
    {
        var expected = new Dictionary<string, string[]>
        {
            ["RunDatabaseAsync"] = [.. OptionsFromCode("RunDatabaseAsync")],
            ["RunInspect"] = [.. OptionsFromCode("RunInspect")],
        };

        string[] files = ["README.md", "README.en.md", Path.Combine("docs", "index.html")];
        foreach (string file in files)
        {
            string text = ReadRepoFile(file);
            foreach ((string method, string[] options) in expected)
            {
                foreach (string option in options)
                {
                    Assert.True(
                        text.Contains("--" + option, StringComparison.Ordinal),
                        $"{file} never mentions --{option}, accepted by {method}.");
                }
            }
        }
    }

    private static void AssertSameOptions(
        IReadOnlySet<string> code,
        IReadOnlySet<string> documented,
        string where)
    {
        Assert.NotEmpty(code);

        string[] undocumented = [.. code.Except(documented).Order()];
        string[] stale = [.. documented.Except(code).Order()];

        Assert.True(
            undocumented.Length == 0,
            $"{where} is missing options the CLI accepts: " +
            string.Join(", ", undocumented.Select(o => "--" + o)));

        Assert.True(
            stale.Length == 0,
            $"{where} documents options the CLI no longer accepts: " +
            string.Join(", ", stale.Select(o => "--" + o)));
    }

    /// <summary>
    /// Names of the CliApplication methods whose EnsureOnly(...) call declares at least one
    /// option, sorted, resolved by walking back to the nearest enclosing method declaration.
    /// </summary>
    private static string[] MethodsAcceptingOptions()
    {
        string source = ReadRepoFile(Path.Combine("src", "DBFSync", "CliApplication.cs"));
        MatchCollection declarations = Regex.Matches(
            source,
            @"private\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?\.]+\s+(\w+)\s*\(");

        var methods = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match call in Regex.Matches(source, @"\bEnsureOnly\((?<args>[^;]*?)\);", RegexOptions.Singleline))
        {
            if (call.Groups["args"].Value.Trim().Length == 0)
                continue;

            Match? owner = declarations
                .Where(declaration => declaration.Index < call.Index)
                .OrderBy(declaration => declaration.Index)
                .LastOrDefault();

            Assert.NotNull(owner);
            methods.Add(owner.Groups[1].Value);
        }

        return [.. methods];
    }

    /// <summary>Option names inside the EnsureOnly(...) call of the named command method.</summary>
    private static IReadOnlySet<string> OptionsFromCode(string method)
    {
        string source = ReadRepoFile(Path.Combine("src", "DBFSync", "CliApplication.cs"));

        // Match the declaration, never a call site: a call site is preceded by await or =.
        Match declaration = Regex.Match(
            source,
            @"private\s+(?:async\s+)?[\w<>\[\]?\.]+\s+" + Regex.Escape(method) + @"\s*\(");
        Assert.True(declaration.Success, $"{method} is not declared in CliApplication.cs.");

        int call = source.IndexOf("EnsureOnly(", declaration.Index, StringComparison.Ordinal);
        Assert.True(call > 0, $"{method} does not call EnsureOnly.");

        int end = source.IndexOf(");", call, StringComparison.Ordinal);
        Assert.True(end > call, $"The EnsureOnly call in {method} is not terminated.");

        return Regex.Matches(source[call..end], "\"([a-z0-9-]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Option names in the first cell of each row of the marked pocket-guide table.</summary>
    private static IReadOnlySet<string> OptionsFromHtmlTable(string key)
    {
        string html = ReadRepoFile(Path.Combine("docs", "index.html"));

        Match table = Regex.Match(
            html,
            "<table[^>]*data-options=\"" + Regex.Escape(key) + "\"[^>]*>(.*?)</table>",
            RegexOptions.Singleline);
        Assert.True(table.Success, $"docs/index.html has no table marked data-options=\"{key}\".");

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match row in Regex.Matches(table.Groups[1].Value, "<tr>(.*?)</tr>", RegexOptions.Singleline))
        {
            Match firstCell = Regex.Match(row.Groups[1].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline);
            if (!firstCell.Success)
                continue;

            foreach (Match option in Regex.Matches(firstCell.Groups[1].Value, "--([a-z0-9-]+)"))
                found.Add(option.Groups[1].Value);
        }

        return found;
    }

    /// <summary>Option names in the first column of the Markdown table under the given heading.</summary>
    private static IReadOnlySet<string> OptionsFromMarkdownTable(string file, string heading)
    {
        string[] lines = ReadRepoFile(file).Split('\n');

        int start = Array.FindIndex(lines, line => line.TrimEnd('\r').Trim() == heading);
        Assert.True(start >= 0, $"{file} has no heading \"{heading}\".");

        var found = new HashSet<string>(StringComparer.Ordinal);
        bool inTable = false;
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r').Trim();
            if (!line.StartsWith('|'))
            {
                if (inTable)
                    break;
                if (line.StartsWith("#", StringComparison.Ordinal))
                    break;
                continue;
            }

            inTable = true;

            // An escaped pipe inside a cell must not be treated as a column separator.
            string[] cells = line.Replace("\\|", "", StringComparison.Ordinal).Split('|');
            if (cells.Length < 2)
                continue;

            string first = cells[1];
            if (first.Trim().Length == 0 || first.Trim().All(c => c is '-' or ':'))
                continue;

            Match option = Regex.Match(first, "--([a-z0-9-]+)");
            if (option.Success)
                found.Add(option.Groups[1].Value);
        }

        Assert.True(inTable, $"{file} has no table under \"{heading}\".");
        return found;
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DBFSync.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new InvalidOperationException(
                $"DBFSync.slnx was not found above {AppContext.BaseDirectory}.");
    }
}
