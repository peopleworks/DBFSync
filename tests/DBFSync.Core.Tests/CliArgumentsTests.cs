using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class CliArgumentsTests
{
    [Fact]
    public void ParsesFlagsEqualsAndRepeatedOptions()
    {
        CliArguments options = CliArguments.Parse(
        [
            "perfil-a",
            "--all",
            "--batch-size=2500",
            "--table",
            "clientes",
            "--table",
            "articulos"
        ]);

        Assert.Equal(["perfil-a"], options.Positionals);
        Assert.True(options.GetFlag("all"));
        Assert.Equal(2500, options.GetInt("batch-size", 100));
        Assert.Equal(["clientes", "articulos"], options.GetMany("table"));
    }

    [Fact]
    public void RejectsUnknownOption()
    {
        CliArguments options = CliArguments.Parse(["--unknown"]);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => options.EnsureOnly("profile"));

        Assert.Contains("--unknown", exception.Message, StringComparison.Ordinal);
    }
}

