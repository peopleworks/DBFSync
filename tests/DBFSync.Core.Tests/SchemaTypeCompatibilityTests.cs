using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class SchemaTypeCompatibilityTests
{
    [Theory]
    [InlineData("nvarchar(20)", "nvarchar(40)", true)]
    [InlineData("nvarchar(20)", "nvarchar(max)", true)]
    [InlineData("nvarchar(max)", "nvarchar(40)", false)]
    [InlineData("nvarchar(40)", "nvarchar(20)", false)]
    [InlineData("decimal(10, 2)", "decimal(12, 4)", true)]
    [InlineData("decimal(10, 2)", "decimal(10, 4)", false)]
    [InlineData("date", "datetime2(3)", true)]
    [InlineData("bigint", "nvarchar(20)", false)]
    public void EvaluatesSqlServerWidening(
        string actual,
        string expected,
        bool canWiden)
    {
        Assert.Equal(
            canWiden,
            SchemaTypeCompatibility.CanWiden(
                DatabaseEngine.SqlServer,
                actual,
                expected));
    }

    [Theory]
    [InlineData("character varying(20)", "character varying(40)", true)]
    [InlineData("character varying(20)", "text", true)]
    [InlineData("text", "character varying(40)", false)]
    [InlineData("numeric(10,2)", "numeric(12,4)", true)]
    [InlineData("numeric(10,2)", "numeric(10,4)", false)]
    [InlineData("date", "timestamp without time zone", true)]
    [InlineData("bigint", "text", false)]
    public void EvaluatesPostgreSqlWidening(
        string actual,
        string expected,
        bool canWiden)
    {
        Assert.Equal(
            canWiden,
            SchemaTypeCompatibility.CanWiden(
                DatabaseEngine.PostgreSql,
                actual,
                expected));
    }

    [Fact]
    public void SQLiteAcceptsOnlyTheSameStorageType()
    {
        Assert.True(SchemaTypeCompatibility.CanWiden(
            DatabaseEngine.SQLite,
            "TEXT",
            "text"));
        Assert.False(SchemaTypeCompatibility.CanWiden(
            DatabaseEngine.SQLite,
            "INTEGER",
            "REAL"));
    }
}
