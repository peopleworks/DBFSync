namespace PeopleWorks.DBFSync;

public static class SqlIdentifier
{
    public static void Validate(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 63 ||
            !(char.IsLetter(value[0]) || value[0] == '_') ||
            value.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new ArgumentException(L10n.T(
                "InvalidSqlIdentifier",
                L10n.T(description),
                value));
    }

    public static string Normalize(string value, string description)
    {
        string normalized = value.Trim().ToLowerInvariant();
        Validate(normalized, description);
        return normalized;
    }

    public static string SqlServer(string value) => $"[{value.Replace("]", "]]")}]";
    public static string PostgreSql(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
