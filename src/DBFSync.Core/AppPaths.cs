namespace PeopleWorks.DBFSync;

public sealed class AppPaths
{
    public AppPaths(string? home = null)
    {
        Home = Path.GetFullPath(home ?? ResolveDefaultHome());
    }

    public string Home { get; }
    public string ProfilesFile => Path.Combine(Home, "profiles.json");
    public string LogsDirectory => Path.Combine(Home, "logs");

    private static string ResolveDefaultHome()
    {
        string? configured = Environment.GetEnvironmentVariable("DBFSYNC_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
            throw new InvalidOperationException(L10n.T("ProgramDataUnavailable"));
        return Path.Combine(programData, "PeopleWorks", "DBFSync");
    }
}
