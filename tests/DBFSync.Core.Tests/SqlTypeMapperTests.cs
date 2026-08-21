using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class SqlTypeMapperTests
{
    [Theory]
    [InlineData(DbfColumnKind.Text, "nvarchar(30)", "varchar(30)")]
    [InlineData(DbfColumnKind.Boolean, "bit", "boolean")]
    [InlineData(DbfColumnKind.Date, "date", "date")]
    [InlineData(DbfColumnKind.Integer, "bigint", "bigint")]
    public void MapsCommonDbfTypes(
        DbfColumnKind kind,
        string sqlServer,
        string postgreSql)
    {
        var column = new DbfColumn("campo", "campo", kind, 30, 12, 2, typeof(object));

        Assert.Equal(sqlServer, SqlTypeMapper.SqlServer(column));
        Assert.Equal(postgreSql, SqlTypeMapper.PostgreSql(column));
    }

    [Fact]
    public void MapsDecimalPrecisionAndScale()
    {
        var column = new DbfColumn(
            "monto",
            "monto",
            DbfColumnKind.Decimal,
            12,
            12,
            2,
            typeof(decimal));

        Assert.Equal("decimal(12, 2)", SqlTypeMapper.SqlServer(column));
        Assert.Equal("numeric(12, 2)", SqlTypeMapper.PostgreSql(column));
        Assert.Equal("numeric(12,2)", SqlTypeMapper.PostgreSqlCatalog(column));
    }

    [Theory]
    [InlineData(DbfColumnKind.Text, "TEXT")]
    [InlineData(DbfColumnKind.Decimal, "TEXT")]
    [InlineData(DbfColumnKind.Integer, "INTEGER")]
    [InlineData(DbfColumnKind.Double, "REAL")]
    [InlineData(DbfColumnKind.Boolean, "INTEGER")]
    [InlineData(DbfColumnKind.Date, "TEXT")]
    [InlineData(DbfColumnKind.DateTime, "TEXT")]
    [InlineData(DbfColumnKind.Binary, "BLOB")]
    public void MapsSQLiteStorageTypes(DbfColumnKind kind, string expected)
    {
        var column = new DbfColumn(
            "campo",
            "campo",
            kind,
            30,
            12,
            2,
            typeof(object));

        Assert.Equal(expected, SqlTypeMapper.SQLite(column));
    }
}
