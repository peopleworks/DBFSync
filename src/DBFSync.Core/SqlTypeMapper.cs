namespace PeopleWorks.DBFSync;

public static class SqlTypeMapper
{
    public static string SqlServer(DbfColumn column) =>
        column.Kind switch
        {
            DbfColumnKind.Text when column.Length is > 0 and <= 4000 =>
                $"nvarchar({column.Length})",
            DbfColumnKind.Text => "nvarchar(max)",
            DbfColumnKind.Decimal => $"decimal({Precision(column)}, {Scale(column)})",
            DbfColumnKind.Integer => "bigint",
            DbfColumnKind.Double => "float(53)",
            DbfColumnKind.Boolean => "bit",
            DbfColumnKind.Date => "date",
            DbfColumnKind.DateTime => "datetime2(3)",
            DbfColumnKind.Binary => "varbinary(max)",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Kind, null)
        };

    public static string PostgreSql(DbfColumn column) =>
        column.Kind switch
        {
            DbfColumnKind.Text when column.Length is > 0 and <= 10_485_760 =>
                $"varchar({column.Length})",
            DbfColumnKind.Text => "text",
            DbfColumnKind.Decimal => $"numeric({Precision(column)}, {Scale(column)})",
            DbfColumnKind.Integer => "bigint",
            DbfColumnKind.Double => "double precision",
            DbfColumnKind.Boolean => "boolean",
            DbfColumnKind.Date => "date",
            DbfColumnKind.DateTime => "timestamp without time zone",
            DbfColumnKind.Binary => "bytea",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Kind, null)
        };

    public static string PostgreSqlCatalog(DbfColumn column) =>
        column.Kind switch
        {
            DbfColumnKind.Text when column.Length is > 0 and <= 10_485_760 =>
                $"character varying({column.Length})",
            DbfColumnKind.Decimal => $"numeric({Precision(column)},{Scale(column)})",
            _ => PostgreSql(column)
        };

    public static string SQLite(DbfColumn column) =>
        column.Kind switch
        {
            DbfColumnKind.Text => "TEXT",
            DbfColumnKind.Decimal => "TEXT",
            DbfColumnKind.Integer => "INTEGER",
            DbfColumnKind.Double => "REAL",
            DbfColumnKind.Boolean => "INTEGER",
            DbfColumnKind.Date or DbfColumnKind.DateTime => "TEXT",
            DbfColumnKind.Binary => "BLOB",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Kind, null)
        };

    public static Type DataColumnType(DbfColumn column) =>
        column.Kind switch
        {
            DbfColumnKind.Text => typeof(string),
            DbfColumnKind.Decimal => typeof(decimal),
            DbfColumnKind.Integer => typeof(long),
            DbfColumnKind.Double => typeof(double),
            DbfColumnKind.Boolean => typeof(bool),
            DbfColumnKind.Date or DbfColumnKind.DateTime => typeof(DateTime),
            DbfColumnKind.Binary => typeof(byte[]),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Kind, null)
        };

    private static int Precision(DbfColumn column) =>
        Math.Clamp(column.Precision > 0 ? column.Precision : 38, 1, 38);

    private static int Scale(DbfColumn column) =>
        Math.Clamp(column.Scale, 0, Precision(column));
}
