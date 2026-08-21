using System.Globalization;
using System.Resources;

namespace PeopleWorks.DBFSync;

public static class L10n
{
    private static readonly ResourceManager Resources = new(
        "PeopleWorks.DBFSync.Resources.Strings",
        typeof(L10n).Assembly);

    public static string[] Configure(string[] args)
    {
        string? requestedCulture = Environment.GetEnvironmentVariable("DBFSYNC_LANG");
        var remaining = new List<string>(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--language=", StringComparison.OrdinalIgnoreCase))
            {
                requestedCulture = argument[(argument.IndexOf('=') + 1)..];
                continue;
            }
            if (argument.Equals("--lang", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--language", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(T("LanguageMissing"));
                requestedCulture = args[++i];
                continue;
            }
            remaining.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(requestedCulture))
            SetCulture(requestedCulture);
        return remaining.ToArray();
    }

    public static void SetCulture(string cultureName)
    {
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName.Trim());
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException ex)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    T("LanguageInvalid"),
                    cultureName),
                ex);
        }
    }

    public static string T(string key, params object?[] arguments)
    {
        string format = Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";
        return arguments.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    public static string ColumnKind(DbfColumnKind kind) =>
        T("ColumnKind" + kind);
}
