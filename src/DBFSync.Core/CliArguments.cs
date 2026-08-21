namespace PeopleWorks.DBFSync;

public sealed class CliArguments
{
    private readonly Dictionary<string, List<string>> _options =
        new(StringComparer.OrdinalIgnoreCase);

    private CliArguments()
    {
    }

    public IReadOnlyList<string> Positionals { get; private set; } = [];

    public static CliArguments Parse(IEnumerable<string> arguments)
    {
        string[] values = arguments.ToArray();
        var parsed = new CliArguments();
        var positionals = new List<string>();

        for (int i = 0; i < values.Length; i++)
        {
            string token = values[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            string option = token[2..];
            string value;
            int equals = option.IndexOf('=');
            if (equals >= 0)
            {
                value = option[(equals + 1)..];
                option = option[..equals];
            }
            else if (i + 1 < values.Length &&
                     !values[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = values[++i];
            }
            else
            {
                value = "true";
            }

            if (string.IsNullOrWhiteSpace(option))
                throw new ArgumentException(L10n.T("InvalidOption", token));
            if (!parsed._options.TryGetValue(option, out List<string>? list))
            {
                list = [];
                parsed._options.Add(option, list);
            }
            list.Add(value);
        }

        parsed.Positionals = positionals;
        return parsed;
    }

    public string? Get(string name) =>
        _options.TryGetValue(name, out List<string>? values) ? values[^1] : null;

    public string GetRequired(string name) =>
        Get(name) is { Length: > 0 } value && value != "true"
            ? value
            : throw new ArgumentException(L10n.T("MissingOption", name));

    public bool GetFlag(string name)
    {
        string? value = Get(name);
        if (value is null)
            return false;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;
        throw new ArgumentException(L10n.T("FlagValueInvalid", name, value));
    }

    public int GetInt(string name, int defaultValue)
    {
        string? value = Get(name);
        if (value is null)
            return defaultValue;
        return int.TryParse(value, out int number)
            ? number
            : throw new ArgumentException(L10n.T("IntegerOptionRequired", name));
    }

    public IReadOnlyList<string> GetMany(string name) =>
        _options.TryGetValue(name, out List<string>? values) ? values : [];

    public void EnsureOnly(params string[] allowed)
    {
        var accepted = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] unknown = _options.Keys.Where(key => !accepted.Contains(key)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(
                L10n.T(
                    "UnknownOptions",
                    string.Join(", ", unknown.Select(key => "--" + key))));
    }
}
