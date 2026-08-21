using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class DbfSelectionTests
{
    [Fact]
    public void ResolvesExactNamesWildcardsAndExclusions()
    {
        string directory = CreateDbfDirectory(
            "fahdfcf0.dbf",
            "fahhfcf0.DBF",
            "famemo.dbf",
            "cbmovf00.dbf");
        try
        {
            var source = new DbfSource();

            IReadOnlyList<string> selected = source.ResolveTables(
                directory,
                ["fa*.dbf", "cbmovf00"],
                ["*memo*"],
                allTables: false);

            Assert.Equal(["fahdfcf0", "fahhfcf0", "cbmovf00"], selected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsQuestionMarkAndInlineNegation()
    {
        string directory = CreateDbfDirectory(
            "cbmovf00.dbf",
            "cbmovf01.dbf",
            "cbmovf10.dbf");
        try
        {
            var source = new DbfSource();

            IReadOnlyList<string> selected = source.ResolveTables(
                directory,
                ["cbmovf??", "!*10"],
                [],
                allTables: false);

            Assert.Equal(["cbmovf00", "cbmovf01"], selected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AllCanBeCombinedWithExclusions()
    {
        string directory = CreateDbfDirectory(
            "clientes.dbf",
            "articulos.dbf",
            "historico.dbf");
        try
        {
            var source = new DbfSource();

            IReadOnlyList<string> selected = source.ResolveTables(
                directory,
                [],
                ["hist*"],
                allTables: true);

            Assert.Equal(["articulos", "clientes"], selected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDbfDirectory(params string[] names)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "DBFSync.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        foreach (string name in names)
            File.WriteAllBytes(Path.Combine(path, name), []);
        return path;
    }
}
