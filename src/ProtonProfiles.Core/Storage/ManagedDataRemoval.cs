using ProtonProfiles.Core.Credentials;

namespace ProtonProfiles.Core.Storage;

/// <summary>Coordinates application instances with an explicit uninstall data-removal operation.</summary>
public static class ManagedDataRemoval
{
    // Outside Root: the exclusive lease remains held while Root (including Locks) is removed.
    public static FileStream AcquireLease(ManagedPaths paths, bool exclusive)
    {
        var parent = Directory.GetParent(paths.Root)?.FullName ?? throw new ArgumentException("A data root cannot be a volume root.");
        Directory.CreateDirectory(parent);
        var file = Path.Combine(parent, "." + Path.GetFileName(paths.Root) + ".use.lock");
        return new(file, FileMode.OpenOrCreate, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.None : FileShare.Read);
    }

    /// <summary>Caller must also confirm no orphan Runtime is using Root. Never follows directory links.</summary>
    public static CleanupResult Remove(ManagedPaths paths, ICredentialStore credentials, IEnumerable<Guid> credentialProfiles)
        => Remove(paths, credentials, credentialProfiles, static () => { });

    /// <summary>Checks Runtime ownership while holding the exclusive application lease, before deleting anything.</summary>
    public static CleanupResult Remove(ManagedPaths paths, ICredentialStore credentials, IEnumerable<Guid> credentialProfiles,
        Action confirmNoProcesses)
    {
        ArgumentNullException.ThrowIfNull(confirmNoProcesses);
        for (var dir = new DirectoryInfo(paths.Root); dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null || (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                return new(CleanupOutcome.RejectedUnsafePath, [paths.Root], "Каталог данных или его родитель является ссылкой.");
        if (File.Exists(paths.Root) && !Directory.Exists(paths.Root))
            return new(CleanupOutcome.RejectedUnsafePath, [paths.Root], "Вместо каталога данных обнаружен файл.");
        if (!Directory.Exists(paths.Root))
        {
            using var absentLease = AcquireLease(paths, exclusive: true);
            confirmNoProcesses();
            foreach (var id in credentialProfiles.Distinct()) credentials.DeleteAllForProfile(id);
            return new(CleanupOutcome.NothingToDelete, [], null);
        }

        using var lease = AcquireLease(paths, exclusive: true);
        confirmNoProcesses();
        var ids = credentialProfiles.ToHashSet();
        foreach (var folder in new[] { paths.ProfilesRoot, paths.LocksRoot })
        {
            if (!Directory.Exists(folder)) continue;
            if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                return new(CleanupOutcome.RejectedUnsafePath, [folder], "Управляемый каталог является ссылкой.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(entry), out var id) && id != Guid.Empty) ids.Add(id);
        }
        var locks = new List<ProfileLock>();
        try
        {
            foreach (var id in ids)
            {
                if (!ProfileLock.TryAcquire(paths, id, out var acquired))
                    return new(CleanupOutcome.Pending, [paths.LockFile(id)], "Профиль используется. Закройте приложение и повторите удаление.");
                locks.Add(acquired!);
            }
            var remaining = new List<string>();
            foreach (var entry in new DirectoryInfo(paths.Root).GetFileSystemInfos())
            {
                if (string.Equals(entry.FullName, paths.LocksRoot, ManagedPaths.PathComparison)) continue;
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null) entry.Delete();
                    else if (entry is DirectoryInfo directory) SafeProfileDeleter.DeleteTree(directory, remaining);
                    else { entry.Attributes &= ~FileAttributes.ReadOnly; entry.Delete(); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { remaining.Add(entry.FullName); }
            }
            if (remaining.Count != 0) return new(CleanupOutcome.Pending, remaining, "Не все данные удалены. Закройте использующие их процессы и повторите удаление.");
            foreach (var id in ids) credentials.DeleteAllForProfile(id);
        }
        finally { foreach (var acquired in locks) acquired.Dispose(); }
        var finalRemaining = new List<string>();
        SafeProfileDeleter.DeleteTree(new DirectoryInfo(paths.Root), finalRemaining);
        return finalRemaining.Count == 0 ? new(CleanupOutcome.Completed, [], null)
            : new(CleanupOutcome.Pending, finalRemaining, "Не удалось удалить все файлы каталога данных.");
    }
}
