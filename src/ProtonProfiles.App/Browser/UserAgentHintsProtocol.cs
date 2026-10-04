using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.App.Browser;

/// <summary>Native UA metadata restriction and optional CPU normalization in related targets. No JS API replacements.</summary>
internal sealed class UserAgentHintsProtocol
{
    private readonly CoreWebView2 _core;
    private readonly string _arguments;
    private readonly string _workerArguments;
    private readonly string? _cpuArguments;
    private readonly string? _timeZoneArguments;
    private readonly bool _restrictUserAgent;
    private readonly bool _standardizeDocuments;
    private readonly bool _blockServiceWorkers;
    private readonly PrivacyException _exceptions;
    private readonly ClientHintsRequests? _clientHints;
    private readonly ResidualWorkerProtocol? _residualWorkers;
    private readonly Func<bool> _current;
    private readonly Func<string, Task>? _onFailure;
    private readonly Action<string>? _diagnostic;
    private readonly HashSet<string> _sessions = [""];
    private const string AutoAttachArguments = "{\"autoAttach\":true,\"waitForDebuggerOnStart\":true,\"flatten\":true}";
    public UserAgentHintsProtocol(CoreWebView2 core, string userAgent, Func<bool> current, Func<string, Task>? onFailure, Action<string>? diagnostic, int? hardwareConcurrency = null, bool standardizeDocuments = false, string? timeZoneId = null, bool restrictUserAgent = true, PrivacyException exceptions = PrivacyException.None)
    {
        _core=core;_current=current;_onFailure=onFailure;_diagnostic=diagnostic;
        _standardizeDocuments=standardizeDocuments;
        _exceptions=exceptions;
        _blockServiceWorkers=standardizeDocuments&&!ProfilePrivacy.Allows(exceptions,PrivacyException.ServiceWorkers);
        _restrictUserAgent=restrictUserAgent;
        if (BrowserTimeZone.Validate(timeZoneId) is { } error) throw new ArgumentException(error,nameof(timeZoneId));
        _timeZoneArguments=timeZoneId is null ? null : JsonSerializer.Serialize(new {timezoneId=timeZoneId});
        _clientHints=standardizeDocuments ? ClientHintsRequests.ForCore(core,Current,onFailure) : null;
        _residualWorkers=standardizeDocuments ? new ResidualWorkerProtocol(core,Current,onFailure,diagnostic,exceptions) : null;
        _cpuArguments=hardwareConcurrency is null ? null : JsonSerializer.Serialize(new {hardwareConcurrency=hardwareConcurrency.Value});
        // Omit userAgentMetadata: CDP then omits UA Client Hints, rather than inventing brand/platform values.
        _arguments=JsonSerializer.Serialize(new {userAgent});
        // WorkerGlobalScope falls back to its creation metadata when the optional
        // override is absent. Provide an explicitly empty native metadata object;
        // every optional field is included so Chromium cannot fill it from defaults.
        _workerArguments=JsonSerializer.Serialize(new {userAgent,userAgentMetadata=new {
            brands=Array.Empty<object>(),fullVersionList=Array.Empty<object>(),fullVersion="",
            platform="",platformVersion="",architecture="",model="",mobile=false,
            bitness="",wow64=false,formFactors=Array.Empty<string>()}});
    }
    private bool Current() { try { return _current() && _core.BrowserProcessId > 0; } catch { return false; } }
    public async Task InitializeAsync()
    {
        if (_standardizeDocuments)
        {
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(ResidualFingerprintPrivacy.ScriptFor(_exceptions));
            if(_blockServiceWorkers)await _core.CallDevToolsProtocolMethodAsync("Network.setBypassServiceWorker","{\"bypass\":true}");
        }
        if (_restrictUserAgent) await _core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",_arguments);
        if (_timeZoneArguments is not null) await _core.CallDevToolsProtocolMethodAsync("Emulation.setTimezoneOverride",_timeZoneArguments).WaitAsync(TimeSpan.FromSeconds(10));
        if (_cpuArguments is not null) await _core.CallDevToolsProtocolMethodAsync("Emulation.setHardwareConcurrencyOverride",_cpuArguments);
        if (_clientHints is not null) await _clientHints.ConfigureAsync();
        if (_standardizeDocuments) await PrepareDocumentAsync(null);
        _core.GetDevToolsProtocolEventReceiver("Target.attachedToTarget").DevToolsProtocolEventReceived += Attached;
        _core.GetDevToolsProtocolEventReceiver("Target.detachedFromTarget").DevToolsProtocolEventReceived += (_,e) => {
            if (!_sessions.Contains(e.SessionId)) return;
            using var doc=JsonDocument.Parse(e.ParameterObjectAsJson);
            if (doc.RootElement.TryGetProperty("sessionId",out var id)) { _sessions.Remove(id.GetString()!);_clientHints?.Forget(id.GetString()!);_residualWorkers?.Forget(id.GetString()!); }
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
            if (type == "service_worker")
            {
                if (_blockServiceWorkers)
                {
                    // Service-worker targets attach before their renderer/context exists. Debugger
                    // instrumentation there aborts the main-script fetch on this Runtime.
                    // Close the target without releasing its startup throttle; new registrations
                    // are blocked by the document guard, and page fetches bypass stored workers.
                    using var closed=JsonDocument.Parse(await _core.CallDevToolsProtocolMethodAsync("Target.closeTarget",JsonSerializer.Serialize(new {targetId=target})));
                    if (!closed.RootElement.GetProperty("success").GetBoolean())throw new InvalidOperationException("Service worker could not be stopped.");
                    _sessions.Remove(session);
                    _diagnostic?.Invoke("Strict service worker target: stopped; startup not resumed");
                    return;
                }
                // Service-worker attachment can throttle the main-script fetch before the
                // renderer exists. Queue emulation and recursive attachment first, then release
                // the browser throttle: waiting for the emulation response here would deadlock.
                Task overrideTask = _restrictUserAgent ? _core.CallDevToolsProtocolMethodForSessionAsync(session,"Emulation.setUserAgentOverride",_workerArguments) : Task.CompletedTask;
                Task cpuTask = _cpuArguments is null ? Task.CompletedTask : _core.CallDevToolsProtocolMethodForSessionAsync(session,"Emulation.setHardwareConcurrencyOverride",_cpuArguments);
                Task timeZoneTask = _timeZoneArguments is null ? Task.CompletedTask : _core.CallDevToolsProtocolMethodForSessionAsync(session,"Emulation.setTimezoneOverride",_timeZoneArguments);
                var attachTask = _core.CallDevToolsProtocolMethodForSessionAsync(session,"Target.setAutoAttach",AutoAttachArguments);
                var resumeTask = _core.CallDevToolsProtocolMethodForSessionAsync(session,"Runtime.runIfWaitingForDebugger","{}");
                await Task.WhenAll(overrideTask,cpuTask,timeZoneTask,attachTask,resumeTask).WaitAsync(TimeSpan.FromSeconds(10));
                _diagnostic?.Invoke("UA target service_worker: prepared and resumed");
                return;
            }
            if (type is "page" or "iframe" or "worker" or "shared_worker" or "service_worker")
            {
                if (_restrictUserAgent) await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Emulation.setUserAgentOverride", type is "worker" or "shared_worker" ? _workerArguments : _arguments);
                if (_cpuArguments is not null) await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Emulation.setHardwareConcurrencyOverride",_cpuArguments);
                if (_timeZoneArguments is not null) {
                    await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Emulation.setTimezoneOverride",_timeZoneArguments).WaitAsync(TimeSpan.FromSeconds(10));
                    _diagnostic?.Invoke("Time zone target " + type + ": native override applied before startup resume");
                }
            }
            _diagnostic?.Invoke("UA target " + type + ": override applied");
            // Fetch is a browser-side document handler; worker sessions reject this domain.
            if (_clientHints is not null && type is "page" or "iframe") await _clientHints.ConfigureAsync(session);
            if (_blockServiceWorkers && type is "page" or "iframe") await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Network.setBypassServiceWorker","{\"bypass\":true}");
            if (_standardizeDocuments && type is "page" or "iframe") await PrepareDocumentAsync(session);
            if (_residualWorkers is not null && type is "worker" or "shared_worker") await _residualWorkers.PrepareAsync(session,target);
            await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Target.setAutoAttach",AutoAttachArguments);
            _diagnostic?.Invoke("UA target " + type + ": auto-attach applied");
            if (value.GetProperty("waitingForDebugger").GetBoolean() && Current())
                await _core.CallDevToolsProtocolMethodForSessionAsync(session,"Runtime.runIfWaitingForDebugger","{}");
            _diagnostic?.Invoke("UA target " + type + ": resumed");
        }
        catch (Exception ex)
        {
            if (!Current() || session is not null && !_sessions.Contains(session)) return;
            // A target that disappeared while being prepared cannot run unprotected scripts.
            try
            {
                using var targets=JsonDocument.Parse(await _core.CallDevToolsProtocolMethodAsync("Target.getTargets","{}"));
                if (target is not null && !targets.RootElement.GetProperty("targetInfos").EnumerateArray().Any(t=>t.GetProperty("targetId").GetString()==target)) return;
            }
            catch { if (!Current()) return; }
            // The host closes the current profile on a live setup failure.
            if (_onFailure is not null)
                try { await _onFailure("Не удалось подготовить часовой пояс / UA Client Hints / CPU / CSS в связанном контексте: " + ex.Message); }
                catch { /* Host teardown errors must not escape the async event handler. */ }
        }
    }

    private async Task PrepareDocumentAsync(string? session)
    {
        foreach (var command in StandardFingerprintPrivacy.Commands(ProfilePrivacy.Allows(_exceptions,PrivacyException.LocalFonts)))
        {
            try
            {
                _diagnostic?.Invoke("Native document defaults: applying " + command.Method);
                var task = session is null ? _core.CallDevToolsProtocolMethodAsync(command.Method, command.Arguments)
                    : _core.CallDevToolsProtocolMethodForSessionAsync(session, command.Method, command.Arguments);
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception e) { throw new InvalidOperationException("Не удалось применить " + command.Method + ": " + e.Message,e); }
        }
        _diagnostic?.Invoke("Native document defaults: media/generic fonts/local sources prepared");
    }
}
