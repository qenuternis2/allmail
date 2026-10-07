using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Storage;

/// <summary>Window geometry is local bookkeeping, independent of browser revisions and JSON interchange.</summary>
public sealed class WindowPlacementStore(ManagedPaths paths)
{
    private string FilePath => Path.Combine(paths.Root, "window-placement.json");

    public static bool IsValid(WindowBounds bounds) => double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
        && Math.Abs(bounds.Left) <= 1_000_000 && Math.Abs(bounds.Top) <= 1_000_000
        && double.IsFinite(bounds.Width) && bounds.Width is >= 100 and <= 100_000
        && double.IsFinite(bounds.Height) && bounds.Height is >= 100 and <= 100_000;

    public static WindowBounds Fit(WindowBounds saved, WindowBounds workArea, double minWidth, double minHeight)
    {
        if (!IsValid(saved) || !IsValid(workArea) || !double.IsFinite(minWidth) || !double.IsFinite(minHeight)
            || minWidth <= 0 || minHeight <= 0) throw new ArgumentException("Invalid window geometry.");
        var width = Math.Max(minWidth, Math.Min(saved.Width, workArea.Width));
        var height = Math.Max(minHeight, Math.Min(saved.Height, workArea.Height));
        return saved with { Width = width, Height = height,
            Left = Math.Clamp(saved.Left, workArea.Left, workArea.Left + Math.Max(0, workArea.Width - width)),
            Top = Math.Clamp(saved.Top, workArea.Top, workArea.Top + Math.Max(0, workArea.Height - height)) };
    }

    public WindowBounds? Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 4096) return null;
            var saved = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(FilePath));
            return saved is not null && IsValid(saved) ? saved : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(WindowBounds bounds)
    {
        if (!IsValid(bounds)) throw new ArgumentException("Invalid window geometry.", nameof(bounds));
        Directory.CreateDirectory(paths.Root);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, bounds); stream.Flush(flushToDisk: true); }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
