namespace ProtonProfiles.Core.Storage;

public enum CleanupOutcome
{
    /// <summary>Nothing remains of the managed directory.</summary>
    Completed,
    /// <summary>Directory was already absent.</summary>
    NothingToDelete,
    /// <summary>Some entries are locked or access was denied; cleanup stays pending and is resumable.</summary>
    Pending,
    /// <summary>The path failed ownership/canonical/reparse checks; nothing was deleted.</summary>
    RejectedUnsafePath,
}

public sealed record CleanupResult(CleanupOutcome Outcome, IReadOnlyList<string> Remaining, string? Reason)
{
    public bool IsComplete => Outcome is CleanupOutcome.Completed or CleanupOutcome.NothingToDelete;
}

/// <summary>
/// Deletes only application-owned profile data (spec §4.7, A25). Validates that the target is the canonical managed
/// directory of the given UUID, refuses when the target or any ancestor inside the managed tree is a reparse point,
/// and never follows links found inside the tree (the link itself is removed, its target is untouched).
/// </summary>
public sealed class SafeProfileDeleter
{
    private readonly ManagedPaths _paths;

    public SafeProfileDeleter(ManagedPaths paths) => _paths = paths;

    /// <summary>Reset: removes only the UDF.</summary>
    public CleanupResult DeleteUserDataFolder(Guid profileId) => DeleteOwned(profileId, _paths.UserDataFolder(profileId));

    /// <summary>Delete: removes the whole managed profile directory (which contains the UDF).</summary>
    public CleanupResult DeleteProfileDirectory(Guid profileId) => DeleteOwned(profileId, _paths.ProfileDirectory(profileId));

    /// <summary>Checks a candidate path without deleting. Exposed for the effective-UDF and import checks.</summary>
    public string? ValidateOwnership(Guid profileId, string target)
    {
        var canonicalTarget = ManagedPaths.Canonical(target);
        var profileDir = ManagedPaths.Canonical(_paths.ProfileDirectory(profileId));
        var profilesRoot = ManagedPaths.Canonical(_paths.ProfilesRoot);
        var cmp = ManagedPaths.PathComparison;

        bool isProfileDir = string.Equals(canonicalTarget, profileDir, cmp);
        bool insideProfileDir = canonicalTarget.StartsWith(profileDir + Path.DirectorySeparatorChar, cmp);
        if (!isProfileDir && !insideProfileDir) return "Путь не принадлежит управляемому каталогу этого профиля.";
        if (!profileDir.StartsWith(profilesRoot + Path.DirectorySeparatorChar, cmp)) return "Каталог профиля вне управляемого дерева.";

        // Walk from the managed root down to the target: no component may be a reparse point/junction/symlink.
        var current = profilesRoot;
        if (IsReparsePoint(current)) return "Корневой каталог профилей является точкой повторной обработки.";
        var relative = Path.GetRelativePath(profilesRoot, canonicalTarget);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") return "Обнаружен выход за пределы управляемого каталога.";
            current = Path.Combine(current, part);
            if (IsReparsePoint(current)) return "Каталог профиля содержит ссылку или точку соединения, ведущую за пределы управляемого дерева.";
        }
        return null;
    }

    private CleanupResult DeleteOwned(Guid profileId, string target)
    {
        var rejection = ValidateOwnership(profileId, target);
        if (rejection is not null) return new CleanupResult(CleanupOutcome.RejectedUnsafePath, [target], rejection);
        if (!Directory.Exists(target) && !File.Exists(target)) return new CleanupResult(CleanupOutcome.NothingToDelete, [], null);

        var remaining = new List<string>();
        DeleteTree(new DirectoryInfo(target), remaining);
        return remaining.Count == 0
            ? new CleanupResult(CleanupOutcome.Completed, [], null)
            : new CleanupResult(CleanupOutcome.Pending, remaining, "Некоторые файлы заблокированы или доступ запрещён; очистка будет продолжена позже.");
    }

    internal static void DeleteTree(DirectoryInfo dir, List<string> remaining)
    {
        var before = remaining.Count;
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { remaining.Add(dir.FullName); return; }

        foreach (var entry in entries)
        {
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                {
                    // Remove the link itself; never descend into its target.
                    if (entry is DirectoryInfo d) d.Delete(recursive: false); else entry.Delete();
                }
                else if (entry is DirectoryInfo sub)
                {
                    DeleteTree(sub, remaining);
                }
                else
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                remaining.Add(entry.FullName);
            }
        }

        try
        {
            if (remaining.Count == before) dir.Delete(recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { remaining.Add(dir.FullName); }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) return false;
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true; // Unknown is treated as unsafe.
        }
    }
}
