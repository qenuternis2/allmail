using System.Text;

namespace ProtonProfiles.Core.Downloads;

/// <summary>Sanitizes server-supplied attachment names and resolves collisions (spec §7, A29). Pure; no I/O except existence checks.</summary>
public static class DownloadPaths
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    // Windows-invalid characters, independent of the host OS so tests on Linux match Windows behavior.
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public const int MaxFileNameLength = 180;

    public static string SanitizeFileName(string? suggested)
    {
        var name = suggested ?? string.Empty;
        // Strip any path the server tried to supply.
        var lastSep = name.LastIndexOfAny(['/', '\\']);
        if (lastSep >= 0) name = name[(lastSep + 1)..];

        var sb = new StringBuilder(name.Length);
        // Rune enumeration replaces malformed UTF-16 before normalization can throw.
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune.IsBmp && Array.IndexOf(Invalid, (char)rune.Value) >= 0
                || rune.Value is 0x202E or 0x202D or 0x200E or 0x200F) sb.Append('_');
            else if (rune.IsBmp) sb.Append((char)rune.Value);
            else sb.Append(rune.ToString());
        }
        name = sb.ToString().Normalize(NormalizationForm.FormC).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.All(c => c == '.' || c == '_')) name = "attachment";

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var stemBase = stem.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stemBase)) stem = "_" + stem;
        if (ext.Length > 20) ext = string.Empty;
        if (stem.Length + ext.Length > MaxFileNameLength)
        {
            var length = MaxFileNameLength - ext.Length;
            if (char.IsHighSurrogate(stem[length - 1])) length--;
            stem = stem[..length];
        }
        return stem + ext;
    }

    /// <summary>Returns a non-existing path inside <paramref name="directory"/> using "name (n).ext" on collision.</summary>
    public static string ResolveCollision(string directory, string sanitizedName, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var candidate = Path.Combine(directory, sanitizedName);
        if (!exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(sanitizedName);
        var ext = Path.GetExtension(sanitizedName);
        for (var i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        throw new IOException("Не удалось подобрать свободное имя файла.");
    }

    /// <summary>Ensures a resolved download path stays inside the chosen directory.</summary>
    public static bool IsInside(string directory, string path)
    {
        var dir = Path.GetFullPath(directory);
        if (!Path.EndsInDirectorySeparator(dir)) dir += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(dir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
