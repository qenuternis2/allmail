namespace ProtonProfiles.Core.Storage;

/// <summary>
/// All managed paths derive from the application root and the immutable profile UUID (spec §4.1).
/// Names, email labels and imported data never influence a filesystem path.
/// </summary>
public sealed class ManagedPaths
{
    public string Root { get; }
    public string ProfilesRoot => Path.Combine(Root, "Profiles");
    public string LocksRoot => Path.Combine(Root, "Locks");
    public string DatabasePath => Path.Combine(Root, "profiles.db");
    public string BackupsRoot => Path.Combine(Root, "Backups");
    public string LogsRoot => Path.Combine(Root, "Logs");

    public ManagedPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root is required.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary><c>%LOCALAPPDATA%\ProtonProfiles</c> on Windows.</summary>
    public static ManagedPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtonProfiles"));

    public string ProfileDirectory(Guid profileId) => Path.Combine(ProfilesRoot, RequireId(profileId));

    public string UserDataFolder(Guid profileId) => Path.Combine(ProfileDirectory(profileId), "WebViewData");

    /// <summary>Local connection logs; deleted with the profile, kept on session reset.</summary>
    public string ProfileLogDirectory(Guid profileId) => Path.Combine(ProfileDirectory(profileId), "Logs");

    public string LockFile(Guid profileId) => Path.Combine(LocksRoot, RequireId(profileId) + ".lock");

    public void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(ProfilesRoot);
        Directory.CreateDirectory(LocksRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    /// <summary>Canonical comparison of an effective UDF reported by the engine against the intended path (spec §4.5 step 3).</summary>
    public bool IsExpectedUserDataFolder(Guid profileId, string? effective)
    {
        if (string.IsNullOrWhiteSpace(effective)) return false;
        var expected = Canonical(UserDataFolder(profileId));
        var actual = Canonical(effective);
        return string.Equals(expected, actual, PathComparison);
    }

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string RequireId(Guid id) =>
        id == Guid.Empty ? throw new ArgumentException("Empty profile id.", nameof(id)) : id.ToString("D");
}
