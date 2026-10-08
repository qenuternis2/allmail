using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ProtonProfiles.App.Browser;

/// <summary>Reconcile a stale SDK state only with Chromium's terminal event and the chosen final file.</summary>
internal static class BrowserDownloadCompletion
{
    internal static (Guid Id, long? Bytes)? ReadProgress(string json)
    {
        try
        {
            using var data = JsonDocument.Parse(json);
            var progress = data.RootElement;
            if (progress.ValueKind != JsonValueKind.Object || !progress.TryGetProperty("guid", out var guid) || guid.ValueKind != JsonValueKind.String
                || !Guid.TryParse(guid.GetString(), out var id)
                || !progress.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String) return null;
            if (state.GetString() is "inProgress" or "canceled") return (id, null);
            if (state.GetString() != "completed"
                || !progress.TryGetProperty("receivedBytes", out var received) || received.ValueKind != JsonValueKind.Number || !received.TryGetDecimal(out var bytes)
                || bytes < 0 || bytes > long.MaxValue || decimal.Truncate(bytes) != bytes
                || !progress.TryGetProperty("totalBytes", out var total) || total.ValueKind != JsonValueKind.Number || !total.TryGetDecimal(out var size)
                || size != bytes) return null;
            return (id, (long)bytes);
        }
        catch (JsonException) { return null; }
    }

    internal static long? Confirm(long? completedBytes, CoreWebView2DownloadState state, bool paused, bool cancelled,
        string chosenPath, string operationPath, long received, long? total)
    {
        // 100%, an existing file, or a protocol inProgress event alone never establish completion.
        // Native interruption (including security checks), pause and user cancellation take precedence.
        if (state != CoreWebView2DownloadState.InProgress || paused || cancelled || completedBytes is not >= 0
            || received > completedBytes || total is not null && total != completedBytes) return null;
        try
        {
            if (!string.Equals(Path.GetFullPath(chosenPath), Path.GetFullPath(operationPath), StringComparison.OrdinalIgnoreCase)) return null;
            // A browser still writing/renaming, a scanner with an exclusive handle, or a missing final
            // file makes this retry later. Never rename/delete a temporary file or disable scanning.
            using var file = new FileStream(chosenPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return file.Length == completedBytes ? completedBytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException) { return null; }
    }
}
