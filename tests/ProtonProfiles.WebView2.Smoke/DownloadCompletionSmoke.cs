using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.App.Browser;
using ProtonProfiles.App.Controls;
using ProtonProfiles.Core.Lifecycle;

internal static class DownloadCompletionSmoke
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "download-completion-policy"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "final.bin"); File.WriteAllBytes(path, new byte[16]);
        long? Check(long? protocol = 16, CoreWebView2DownloadState state = CoreWebView2DownloadState.InProgress,
            bool paused = false, bool cancelled = false, string? operationPath = null, long received = 16, long? total = 16) =>
            BrowserDownloadCompletion.Confirm(protocol, state, paused, cancelled, path, operationPath ?? path, received, total);
        Require(Check() == 16, "stale native InProgress reconciles with terminal event and final file");
        Require(Check(received: 8) == 16 && Check(total: null) == 16, "stale byte counter and unknown length use authoritative final bytes");
        Require(Check(protocol: null) is null, "100% and pre-existing full file without terminal event are insufficient");
        Require(Check(state: CoreWebView2DownloadState.Interrupted) is null && Check(paused: true) is null && Check(cancelled: true) is null,
            "interruption/security failure, pause and cancellation cannot be overridden");
        Require(Check(operationPath: Path.Combine(directory, "other.bin")) is null && Check(received: 17) is null && Check(total: 17) is null,
            "different chosen path and contradictory native size never complete");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(Check() is null, "exclusive scanner/writer lock leaves operation pending");
        Require(Check() == 16, "completion rechecked successfully after exclusive lock release");
        File.WriteAllBytes(path, new byte[8]); Require(Check() is null, "partial final file does not complete");
        File.Delete(path); File.WriteAllBytes(path + ".crdownload", new byte[16]);
        Require(Check() is null, "temporary file alone does not complete and is not renamed");
        Require(File.Exists(path + ".crdownload") && !File.Exists(path), "reconciliation never modifies downloaded files");
        File.WriteAllBytes(path, []); Require(Check(protocol: 0, received: 0, total: null) == 0, "empty complete download supported");

        var id = Guid.NewGuid();
        string Event(string state, object received, object total) => JsonSerializer.Serialize(new { guid = id, state, receivedBytes = received, totalBytes = total });
        Require(BrowserDownloadCompletion.ReadProgress(Event("completed", 16, 16)) == (id, 16L), "parse browser terminal GUID and size");
        Require(BrowserDownloadCompletion.ReadProgress(Event("inProgress", 16, 16)) == (id, (long?)null)
            && BrowserDownloadCompletion.ReadProgress(Event("canceled", 16, 16)) == (id, (long?)null), "nonterminal/cancel resets completion signal");
        foreach (var json in new[] { "{", "null", "[]", "{}", Event("completed", -1, -1), Event("completed", 0.5, 0.5),
            Event("completed", "16", 16), Event("completed", 16, 17), Event("completed", 1e30, 1e30), Event("unknown", 16, 16) })
            Require(BrowserDownloadCompletion.ReadProgress(json) is null, "reject malformed, fractional, overflow or unrecognized progress: " + json);

        var info = new DownloadInfo(new GenerationContext(Guid.NewGuid(), 1), id, "final.bin", DownloadPhase.InProgress,
            FilePath: path, BytesReceived: 16, TotalBytes: 16, BytesPerSecond: 0);
        var item = new DownloadItem(info);
        Require(item.BrowserDetailsVisibility == Visibility.Visible, "pending finalization exposes native browser details");
        Require(item.Status == "Сохранение файла…" && item.Detail.Contains("ожидание завершения браузером") && !item.Detail.Contains("ожидание данных")
            && !item.Detail.Contains("/с") && item.Pending && item.FolderVisibility == Visibility.Collapsed, "finalizing UI stays pending without misleading network-wait text");
        item.Update(info with { BytesReceived = 8 }); Require(item.Detail.Contains("ожидание данных"), "real network stall still explained");
        item.Update(info with { Phase = DownloadPhase.Completed });
        Require(item.BrowserDetailsVisibility == Visibility.Collapsed, "completed item needs no safety decision");
        Require(item.Status == "Готово" && !item.Pending && item.FolderVisibility == Visibility.Visible, "completed UI releases active counter and enables folder");
        item.Update(info with { Phase = DownloadPhase.Interrupted, Resume = null });
        Require(item.BrowserDetailsVisibility == Visibility.Visible, "non-resumable interruption still exposes browser warnings");
        item.Update(info with { Phase = DownloadPhase.Cancelled });
        Require(item.BrowserDetailsVisibility == Visibility.Collapsed, "cancelled/closed profile item has no native action");
        Console.WriteLine("PASS: download completion policy; stale native InProgress; final-file size/path verification; 100% without browser terminal signal remains pending; scanner lock retry; interruption/pause/cancel precedence; partial/missing/temporary file rejection; protocol parsing; finalization UI.");
    }
    private static void Require(bool success, string message) { if (!success) throw new InvalidOperationException("Download completion regression: " + message); }
}
