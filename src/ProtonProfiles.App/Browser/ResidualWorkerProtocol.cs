using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.App.Browser;

// Pause before the first worker script, install restrictions, verify, then resume.
// Service workers use native stop/bypass instead; their early attachment precedes renderer creation.
internal sealed class ResidualWorkerProtocol
{
    private readonly CoreWebView2 _core;
    private readonly PrivacyException _exceptions;
    private readonly Func<bool> _current;
    private readonly Func<string,Task>? _failure;
    private readonly Action<string>? _diagnostic;
    private readonly Dictionary<string,Task<string>> _breakpoints=[];
    private readonly Dictionary<string,string> _targets=[];
    public ResidualWorkerProtocol(CoreWebView2 core,Func<bool> current,Func<string,Task>? failure,Action<string>? diagnostic,PrivacyException exceptions=PrivacyException.None)
    {
        _exceptions=exceptions;
        _core=core;_current=current;_failure=failure;_diagnostic=diagnostic;
        _core.GetDevToolsProtocolEventReceiver("Debugger.paused").DevToolsProtocolEventReceived+=Paused;
    }
    public async Task PrepareAsync(string session,string target)
    {
        var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _breakpoints[session]=ready.Task;
        _targets[session]=target;
        try
        {
            // Queue both commands before the caller resumes the worker target.
            var enable=_core.CallDevToolsProtocolMethodForSessionAsync(session,"Debugger.enable","{}");
            var breakpoint=_core.CallDevToolsProtocolMethodForSessionAsync(session,"Debugger.setInstrumentationBreakpoint","{\"instrumentation\":\"beforeScriptExecution\"}");
            await enable;
            using var doc=JsonDocument.Parse(await breakpoint);
            ready.TrySetResult(doc.RootElement.GetProperty("breakpointId").GetString()!);
        }
        catch(Exception e){ready.TrySetException(e);throw;}
    }
    public void Forget(string session) {_breakpoints.Remove(session);_targets.Remove(session);}
    private async void Paused(object? sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if(!_current() || !_breakpoints.TryGetValue(e.SessionId,out var breakpoint))return;
        _targets.TryGetValue(e.SessionId,out var target);
        try
        {
            var id=await breakpoint;
            await _core.CallDevToolsProtocolMethodForSessionAsync(e.SessionId,"Debugger.removeBreakpoint",JsonSerializer.Serialize(new {breakpointId=id}));
            var installed=await _core.CallDevToolsProtocolMethodForSessionAsync(e.SessionId,"Runtime.evaluate",JsonSerializer.Serialize(new {expression=ResidualFingerprintPrivacy.ScriptFor(_exceptions),returnByValue=true}));
            using(var doc=JsonDocument.Parse(installed))
                if(doc.RootElement.TryGetProperty("exceptionDetails",out _) || doc.RootElement.GetProperty("result").GetProperty("value").ValueKind!=JsonValueKind.True)
                    throw new InvalidOperationException("Worker privacy installation failed.");
            var readback=await _core.CallDevToolsProtocolMethodForSessionAsync(e.SessionId,"Runtime.evaluate",JsonSerializer.Serialize(new {expression=ResidualFingerprintPrivacy.EvaluationScript,returnByValue=true}));
            using var result=JsonDocument.Parse(readback);
            var json=result.RootElement.GetProperty("result").GetProperty("value").GetRawText();
            if(ResidualFingerprintPrivacy.ReadResult(json,_exceptions).Outcome!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Worker privacy readback failed.");
            _diagnostic?.Invoke("Worker residual privacy before first script: "+json);
            // Disabling the debugger resumes this instrumentation pause and stops future
            // site `debugger` statements from parking a worker after startup.
            await _core.CallDevToolsProtocolMethodForSessionAsync(e.SessionId,"Debugger.disable","{}");
            Forget(e.SessionId);
        }
        catch(Exception ex)
        {
            if(!_current() || !_breakpoints.ContainsKey(e.SessionId))return;
            // A resumed worker may close before the Debugger.disable acknowledgement.
            // Confirm that its target is gone before discarding any failed command;
            // failures on a live target still block the current profile.
            try
            {
                using var targets=JsonDocument.Parse(await _core.CallDevToolsProtocolMethodAsync("Target.getTargets","{}"));
                if(target is not null&&!targets.RootElement.GetProperty("targetInfos").EnumerateArray().Any(t=>t.GetProperty("targetId").GetString()==target)){Forget(e.SessionId);return;}
            }
            catch {if(!_current())return;}
            if(!_current() || !_breakpoints.ContainsKey(e.SessionId))return;
            if(_failure is not null)try {await _failure("Не удалось ограничить аппаратные API worker: "+ex.Message);}catch {}
        }
    }
}
