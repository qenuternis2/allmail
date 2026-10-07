using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProtonProfiles.Core.Lifecycle;

namespace ProtonProfiles.Core.Storage;

/// <summary>
/// Interprocess ownership of one profile (spec §4.2, A03). An exclusively opened file handle in the Locks directory,
/// outside the deletable UDF. A persistent PID/creation-time record also rejects ownership while a browser
/// outlives its host. Dead/stale records are removed under the file lock; a lock filename alone is not ownership.
/// Not thread-affine, so it is safe across async continuations.
/// </summary>
public sealed class ProfileLock : IDisposable
{
    private FileStream? _handle;
    private BrowserOwner? _browser;
    private string BrowserFile => Path + ".browser.json";
    private sealed record BrowserOwner(int Version, int ProcessId, long StartTimeUtcTicks);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    // Weak keys avoid retaining closed sessions and preserve the public start-request record/equality contract.
    private static readonly ConditionalWeakTable<BrowserStartRequest, ProfileLock> BrowserStarts = new();
    internal static void RegisterBrowserStart(BrowserStartRequest request, ProfileLock owner) => BrowserStarts.Add(request, owner);
    internal static void RecordBrowserProcess(BrowserStartRequest request, int processId)
    {
        if (BrowserStarts.TryGetValue(request, out var owner)) owner.RecordBrowserProcess(processId);
    }

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
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            var ownerFile = path + ".browser.json";
            if (ReadBrowser(ownerFile) is { } browser)
            {
                if (IsRunning(browser)) { acquired = null; return false; }
                File.Delete(ownerFile);
            }
            // On Windows FileShare.None is a mandatory share lock; on Unix .NET applies flock(LOCK_EX) for it.
            stream.SetLength(0);
            var stamp = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n");
            stream.Write(stamp);
            stream.Flush();
            acquired = new ProfileLock(profileId, path, stream);
            stream = null; // Ownership transferred; failed probes always dispose their file handle.
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
        finally { stream?.Dispose(); }
    }

    private static BrowserOwner? ReadBrowser(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4096) throw new IOException("Запись владельца браузера слишком большая.");
            var owner = JsonSerializer.Deserialize<BrowserOwner>(stream, Json);
            if (owner is not { Version: 1, ProcessId: > 0, StartTimeUtcTicks: > 0 }
                || owner.StartTimeUtcTicks > DateTime.MaxValue.Ticks)
                throw new IOException("Неизвестная запись владельца браузера.");
            return owner;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException error) { throw new IOException("Повреждена запись владельца браузера.", error); }
    }

    private static bool IsRunning(BrowserOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.StartTimeUtcTicks;
        }
        catch (ArgumentException) { return false; } // PID no longer exists.
        catch (InvalidOperationException) { return false; } // Process exited during inspection.
        catch (Win32Exception error) { throw new IOException("Не удалось подтвердить завершение браузера.", error); }
    }

    internal void RecordBrowserProcess(int processId)
    {
        ObjectDisposedException.ThrowIf(_handle is null, this);
        using var process = Process.GetProcessById(processId);
        var owner = new BrowserOwner(1, processId, process.StartTime.ToUniversalTime().Ticks);
        var temporary = BrowserFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, owner, Json); stream.Flush(flushToDisk: true); }
            File.Move(temporary, BrowserFile, overwrite: true);
            _browser = owner;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public bool IsHeld => _handle is not null;

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, null);
        if (h is null) return;
        try
        {
            // A host shutdown/finalizer must not forget a still-live browser. Leave its record for the next instance.
            if (_browser is { } browser && !IsRunning(browser) && ReadBrowser(BrowserFile) == browser) File.Delete(BrowserFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Keep uncertain ownership. */ }
        finally { h.Dispose(); }
    }
}
