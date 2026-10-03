using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ProtonProfiles.App.Browser;

/// <summary>Native UA metadata omission in related targets before their scripts run. No JS API replacements.</summary>
internal sealed class UserAgentHintsProtocol
{
    private readonly CoreWebView2 _core;
    private readonly string _arguments;
    private readonly Func<bool> _current;
    private readonly Func<string, Task>? _onFailure;
    private readonly Action<string>? _diagnostic;
    private readonly HashSet<string> _detached = [];
    private readonly HashSet<string> _sessions = [""];
    private const string AutoAttachArguments = "{\"autoAttach\":true,\"waitForDebuggerOnStart\":true,\"flatten\":true}";
    public UserAgentHintsProtocol(CoreWebView2 core, string userAgent, Func<bool> current, Func<string, Task>? onFailure, Action<string>? diagnostic)
    {
        _core=core;_current=current;_onFailure=onFailure;_diagnostic=diagnostic;
        // Omit userAgentMetadata: CDP then omits UA Client Hints, rather than inventing brand/platform values.
        _arguments=JsonSerializer.Serialize(new {userAgent});
    }
    private bool Current() { try { return _current(); } catch { return false; } }
    public async Task InitializeAsync()
    {
        await _core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",_arguments);
        _core.GetDevToolsProtocolEventReceiver("Target.attachedToTarget").DevToolsProtocolEventReceived += Attached;
        _core.GetDevToolsProtocolEventReceiver("Target.detachedFromTarget").DevToolsProtocolEventReceived += (_,e) => {
            if (!_sessions.Contains(e.SessionId)) return;
            using var doc=JsonDocument.Parse(e.ParameterObjectAsJson);
            if (doc.RootElement.TryGetProperty("sessionId",out var id)) _detached.Add(id.GetString()!);
        };
        // WebView2 exposes a page session, not a browser session. Recursively auto-attach
        // related frame/worker targets, pausing each until native emulation is prepared.
        await _core.CallDevToolsProtocolMethodAsync("Target.setAutoAttach", AutoAttachArguments);
    }
    private async void Attached(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_sessions.Contains(e.SessionId) || !Current()) return;
        string? session=null,target=null;
        try
        {
            using var doc=JsonDocument.Parse(e.ParameterObjectAsJson);
            var value=doc.RootElement;
            session=value.GetProperty("sessionId").GetString()!;
            _sessions.Add(session);
            target=value.GetProperty("targetInfo").GetProperty("targetId").GetString()!;
            var type=value.GetProperty("targetInfo").GetProperty("type").GetString();
            _diagnostic?.Invoke("UA target " + type + ": attached");
            if (type is "page" or "iframe" or "worker" or "shared_worker" or "service_worker")
                await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Emulation.setUserAgentOverride",_arguments);
            _diagnostic?.Invoke("UA target " + type + ": override applied");
            await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Target.setAutoAttach",AutoAttachArguments);
            _diagnostic?.Invoke("UA target " + type + ": auto-attach applied");
            if (value.GetProperty("waitingForDebugger").GetBoolean() && Current())
                await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Runtime.runIfWaitingForDebugger","{}");
            _diagnostic?.Invoke("UA target " + type + ": resumed");
        }
        catch (Exception ex)
        {
            if (!Current() || session is not null && _detached.Contains(session)) return;
            // A target that disappeared while being prepared cannot run unprotected scripts.
            try
            {
                using var targets=JsonDocument.Parse(await _core.CallDevToolsProtocolMethodAsync("Target.getTargets","{}"));
                if (target is not null && !targets.RootElement.GetProperty("targetInfos").EnumerateArray().Any(t=>t.GetProperty("targetId").GetString()==target)) return;
            }
            catch { if (!Current()) return; }
            // Keep an unprepared new target paused; the host closes the current profile on a live setup failure.
            if (_onFailure is not null)
                try { await _onFailure("Не удалось подготовить UA Client Hints в связанном контексте: " + ex.Message); }
                catch { /* The unprepared target remains paused even if host teardown fails. */ }
        }
    }
}
