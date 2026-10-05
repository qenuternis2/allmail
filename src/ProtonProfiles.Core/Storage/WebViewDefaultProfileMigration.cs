namespace ProtonProfiles.Core.Storage;

/// <summary>Cold migration from the old named Default to the Runtime's default BrowserContext.
/// Call only while the lifecycle owns the profile lock and no browser process uses this UDF.</summary>
public static class WebViewDefaultProfileMigration
{
    public const string LegacyDirectory = "WV2Profile_default";
    public const string BackupDirectory = "AllMails-v39-legacy-backup";
    public const string PreviousDefaultDirectory = "AllMails-v39-previous-default";
    private const string Journal = "AllMails-v39-default.pending";
    private const string Marker = "AllMails-v39-default.complete";

    public static void Prepare(string userDataFolder)
    {
        var root = Path.Combine(userDataFolder, "EBWebView");
        if (!Directory.Exists(root)) return;
        RejectLink(root);
        var legacy = Path.Combine(root, LegacyDirectory);
        var current = Path.Combine(root, "Default");
        var previous = Path.Combine(root, PreviousDefaultDirectory);
        var journal = Path.Combine(root, Journal);
        var marker = Path.Combine(root, Marker);
        var commit = marker + ".tmp";
        if (File.Exists(marker)) return;
        // Before the commit marker the app cannot launch a controller, so a pending swap can be rolled back.
        if (File.Exists(journal)) Recover(legacy, current, previous, journal);
        if (!Directory.Exists(legacy)) return;
        RejectLink(legacy);
        if (Directory.Exists(previous)) throw new IOException("Обнаружена незавершённая резервная копия Default; открытие заблокировано.");
        var backup = Path.Combine(root, BackupDirectory);
        CopyDirectory(legacy, backup); // original cookies/SQLite/local storage retained before any rename
        if (Directory.Exists(current)) RejectLink(current);
        File.WriteAllText(commit, "Default profile migration v1 completed; backups retained.");
        File.WriteAllText(journal, "Cold Default profile migration v1");
        try
        {
            if (Directory.Exists(current)) Directory.Move(current, previous);
            Directory.Move(legacy, current);
            File.Move(commit, marker);
        }
        catch
        {
            Recover(legacy, current, previous, journal);
            throw;
        }
        File.Delete(journal);
    }

    private static void Recover(string legacy, string current, string previous, string journal)
    {
        if (!Directory.Exists(legacy) && Directory.Exists(current)) Directory.Move(current, legacy);
        if (Directory.Exists(previous))
        {
            if (Directory.Exists(current)) throw new IOException("Не удалось восстановить Default; все копии сохранены.");
            Directory.Move(previous, current);
        }
        File.Delete(journal);
    }

    private static void CopyDirectory(string source, string destination)
    {
        RejectLink(source);
        if (Directory.Exists(destination)) RejectLink(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            RejectLink(file);
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target)) RejectLink(target);
            File.Copy(file, target, overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Перенос данных профиля через ссылку не поддерживается; открытие заблокировано.");
    }
}
