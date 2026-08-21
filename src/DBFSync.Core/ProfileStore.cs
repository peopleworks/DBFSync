using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeopleWorks.DBFSync;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public ProfileStore(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public IReadOnlyList<ConnectionProfile> List() =>
        Load().Profiles
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public ConnectionProfile GetRequired(string name) =>
        Load().Profiles.SingleOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(L10n.T("ProfileNotFoundStored", name));

    public void Upsert(ConnectionProfile profile)
    {
        profile.Validate();
        ProfileDocument document = Load();
        int existing = document.Profiles.FindIndex(p =>
            p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            document.Profiles[existing] = profile;
        else
            document.Profiles.Add(profile);
        Save(document);
    }

    public bool Remove(string name)
    {
        ProfileDocument document = Load();
        int removed = document.Profiles.RemoveAll(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            Save(document);
        return removed > 0;
    }

    private ProfileDocument Load()
    {
        if (!File.Exists(_path))
            return new ProfileDocument();

        string json = File.ReadAllText(_path, Encoding.UTF8);
        ProfileDocument document = JsonSerializer.Deserialize<ProfileDocument>(json, JsonOptions)
            ?? throw new InvalidDataException(L10n.T("ProfilesFileEmpty", _path));
        if (document.Version != 1)
            throw new InvalidDataException(L10n.T(
                "ProfilesVersionUnsupported",
                document.Version));
        foreach (ConnectionProfile profile in document.Profiles)
            profile.Validate();
        return document;
    }

    private void Save(ProfileDocument document)
    {
        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(L10n.T("ProfilesPathInvalid", _path));
        Directory.CreateDirectory(directory);

        document.Profiles.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine;
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed class ProfileDocument
    {
        public int Version { get; init; } = 1;
        public List<ConnectionProfile> Profiles { get; init; } = [];
    }
}
