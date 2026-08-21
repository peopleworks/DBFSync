using System.Globalization;
using System.Text.RegularExpressions;

namespace PeopleWorks.DBFSync;

internal static partial class SchemaTypeCompatibility
{
    public static bool CanWiden(
        DatabaseEngine engine,
        string actual,
        string expected)
    {
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return true;

        return engine switch
        {
            DatabaseEngine.SqlServer => CanWidenSqlServer(actual, expected),
            DatabaseEngine.PostgreSql => CanWidenPostgreSql(actual, expected),
            DatabaseEngine.SQLite => false,
            _ => false
        };
    }

    private static bool CanWidenSqlServer(string actual, string expected)
    {
        if (actual.Equals("date", StringComparison.OrdinalIgnoreCase) &&
            expected.StartsWith("datetime2(", StringComparison.OrdinalIgnoreCase))
            return true;
        if (TryLength(actual, "nvarchar", out int actualLength) &&
            TryLength(expected, "nvarchar", out int expectedLength))
            return expectedLength == int.MaxValue ||
                   actualLength != int.MaxValue && expectedLength >= actualLength;
        return TryDecimal(actual, out int actualPrecision, out int actualScale) &&
               TryDecimal(expected, out int expectedPrecision, out int expectedScale) &&
               expectedScale >= actualScale &&
               expectedPrecision - expectedScale >= actualPrecision - actualScale;
    }

    private static bool CanWidenPostgreSql(string actual, string expected)
    {
        if (actual.StartsWith("character varying(", StringComparison.OrdinalIgnoreCase) &&
            expected.Equals("text", StringComparison.OrdinalIgnoreCase))
            return true;
        if (actual.Equals("date", StringComparison.OrdinalIgnoreCase) &&
            expected.Equals("timestamp without time zone", StringComparison.OrdinalIgnoreCase))
            return true;
        if (TryLength(actual, "character varying", out int actualLength) &&
            TryLength(expected, "character varying", out int expectedLength))
            return expectedLength >= actualLength;
        return TryDecimal(actual, out int actualPrecision, out int actualScale) &&
               TryDecimal(expected, out int expectedPrecision, out int expectedScale) &&
               expectedScale >= actualScale &&
               expectedPrecision - expectedScale >= actualPrecision - actualScale;
    }

    private static bool TryLength(string value, string type, out int length)
    {
        if (value.Equals(type + "(max)", StringComparison.OrdinalIgnoreCase))
        {
            length = int.MaxValue;
            return true;
        }

        Match match = LengthPattern().Match(value);
        if (match.Success &&
            match.Groups["type"].Value.Equals(type, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                match.Groups["length"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out length))
            return true;
        length = 0;
        return false;
    }

    private static bool TryDecimal(
        string value,
        out int precision,
        out int scale)
    {
        Match match = DecimalPattern().Match(value);
        if (match.Success &&
            int.TryParse(match.Groups["precision"].Value, out precision) &&
            int.TryParse(match.Groups["scale"].Value, out scale))
            return true;
        precision = 0;
        scale = 0;
        return false;
    }

    [GeneratedRegex(
        @"^(?<type>[a-z ]+)\((?<length>\d+)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthPattern();

    [GeneratedRegex(
        @"^(?:decimal|numeric)\((?<precision>\d+),\s*(?<scale>\d+)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();
}

