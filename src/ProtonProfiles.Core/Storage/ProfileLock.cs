namespace ProtonProfiles.Core.Storage;

/// <summary>
/// Interprocess ownership of one profile (spec §4.2, A03). An exclusively opened file handle in the Locks directory,
/// outside the deletable UDF. The OS releases the handle when the owning process dies, so a leftover lock file
/// is not a live lock. Not thread-affine, so it is safe across async continuations.
/// </summary>
public sealed class ProfileLock : IDisposable
{
    private FileStream? _handle;

    public Guid ProfileId { get; }
    public string Path { get; }

    private ProfileLock(Guid profileId, string path, FileStream handle)
    {
        ProfileId = profileId;
        Path = path;
        _handle = handle;
    }

    public static bool TryAcquire(ManagedPaths paths, Guid profileId, out ProfileLock? acquired)
    {
        var path = paths.LockFile(profileId);
        Directory.CreateDirectory(paths.LocksRoot);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            // On Windows FileShare.None is a mandatory share lock; on Unix .NET applies flock(LOCK_EX) for it.
            stream.SetLength(0);
            var stamp = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n");
            stream.Write(stamp);
            stream.Flush();
            acquired = new ProfileLock(profileId, path, stream);
            return true;
        }
        catch (IOException)
        {
            acquired = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            acquired = null;
            return false;
        }
    }

    public bool IsHeld => _handle is not null;

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, null);
        h?.Dispose();
    }
}
