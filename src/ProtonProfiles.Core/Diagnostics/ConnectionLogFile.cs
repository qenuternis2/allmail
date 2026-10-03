using System.Globalization;
using System.Text;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Diagnostics;

/// <summary>
/// Appends a profile's connection log to <c>Profiles\&lt;UUID&gt;\Logs\connections-*.tsv</c> (one file per browser
/// generation). Only the newest <see cref="KeepFiles"/> files are kept. The file lives inside the profile directory so
/// deleting the profile deletes its logs.
/// </summary>
public sealed class ConnectionLogFile : IDisposable
{
    public const int KeepFiles = 10;
    private readonly StreamWriter _writer;
    private readonly ConnectionLog _log;

    public string FilePath { get; }

    private ConnectionLogFile(string path, ConnectionLog log)
    {
        FilePath = path;
        _log = log;
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine(ConnectionLog.TsvHeader);
        _log.Added += OnAdded;
    }

    /// <summary>Starts mirroring <paramref name="log"/> to a new file; returns null if the folder is not writable.</summary>
    public static ConnectionLogFile? TryStart(ManagedPaths paths, Guid profileId, ConnectionLog log, DateTimeOffset now)
    {
        try
        {
            var dir = paths.ProfileLogDirectory(profileId);
            Directory.CreateDirectory(dir);
            Prune(dir, KeepFiles - 1);
            var name = $"connections-{now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            var path = Path.Combine(dir, name + ".tsv");
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name}-{i}.tsv");
            return new ConnectionLogFile(path, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the oldest log files so that at most <paramref name="keep"/> remain.</summary>
    public static void Prune(string directory, int keep)
    {
        if (!Directory.Exists(directory)) return;
        var files = new DirectoryInfo(directory).GetFiles("connections-*.tsv")
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(Math.Max(0, keep));
        foreach (var f in files)
        {
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void OnAdded(ConnectionEntry e)
    {
        try { _writer.WriteLine(ConnectionLog.ToTsv(e)); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _log.Added -= OnAdded;
        _writer.Dispose();
    }
}
