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
    public ResidualWorkerProtocol(CoreWebView2 core,Func<bool> current,Func<string,Task>? failure,Action<string>? diagnostic,PrivacyException exceptions=PrivacyException.None)
    {
        _exceptions=exceptions;
        _core=core;_current=current;_failure=failure;_diagnostic=diagnostic;
        _core.GetDevToolsProtocolEventReceiver("Debugger.paused").DevToolsProtocolEventReceived+=Paused;
    }
    public async Task PrepareAsync(string session)
    {
        var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _breakpoints[session]=ready.Task;
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
    public void Forget(string session)=>_breakpoints.Remove(session);
    private async void Paused(object? sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if(!_current() || !_breakpoints.TryGetValue(e.SessionId,out var breakpoint))return;
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
            _breakpoints.Remove(e.SessionId);
        }
        catch(Exception ex)
        {
            if(!_current() || !_breakpoints.ContainsKey(e.SessionId))return;
            if(_failure is not null)try {await _failure("Не удалось ограничить аппаратные API worker: "+ex.Message);}catch {}
        }
    }
}
