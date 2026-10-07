using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace ProtonProfiles.App.Browser;

/// <summary>Observe Chromium download IDs without changing its download behavior or network route.</summary>
internal sealed class BrowserDownloadIdentity : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly CoreWebView2DevToolsProtocolEventReceiver _receiver;
    private readonly List<(string Uri, Guid Id)> _pending = [];
    private readonly Dictionary<string, List<Guid>> _begun = [];
    private bool _disposed;
    public BrowserDownloadIdentity(CoreWebView2 core)
    {
        _core = core;
        _receiver = core.GetDevToolsProtocolEventReceiver("Page.downloadWillBegin");
        _receiver.DevToolsProtocolEventReceived += Started;
    }
    public Task EnableAsync() => _core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
    private void Started(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (_disposed) return;
        using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
        var uri = data.RootElement.GetProperty("url").GetString();
        if (uri is null || !Guid.TryParse(data.RootElement.GetProperty("guid").GetString(), out var id)) return;
        _pending.Add((uri, id));
        if (_pending.Count > 100) _pending.RemoveAt(0);
    }
    public async Task<Guid?> TakeAsync(string uri, Func<Guid, bool> active)
    {
        // SDK and protocol notifications travel through separate callbacks; let queued events arrive.
        await Task.Delay(100);
        if (_disposed) return null;
        var index = _pending.FindIndex(item => item.Uri == uri);
        if (index >= 0)
        {
            var id = _pending[index].Id; _pending.RemoveAt(index);
            if (!_begun.TryGetValue(uri, out var ids)) _begun[uri] = ids = [];
            if (!ids.Contains(id)) ids.Add(id);
            return id;
        }
        // Native Range retries can raise DownloadStarting without a new downloadWillBegin.
        if (!_begun.TryGetValue(uri, out var previous)) return null;
        var candidates = previous.Where(active).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _receiver.DevToolsProtocolEventReceived -= Started; }
        catch (Exception e) when (e is COMException or InvalidOperationException) { } // Dead Runtime; still release managed bookkeeping.
        _pending.Clear(); _begun.Clear();
    }
}
