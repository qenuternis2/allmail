using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

/// <summary>Strip generated client hints at native Fetch interception before transport.</summary>
internal sealed class ClientHintsRequests
{
    private readonly CoreWebView2 _core;
    private readonly Func<bool> _current;
    private readonly Func<string,Task>? _onFailure;
    private readonly HashSet<string> _sessions = [];
    private readonly HashSet<(string Session,string NetworkId)> _failed = [];
    private static readonly ConditionalWeakTable<CoreWebView2,ClientHintsRequests> Instances = new();
    private bool _stripClientHints;
    public static ClientHintsRequests ForCore(CoreWebView2 core,Func<bool> current,Func<string,Task>? onFailure,bool stripClientHints=true)
    {
        var instance=Instances.GetValue(core,c=>new ClientHintsRequests(c,current,onFailure));
        instance._stripClientHints |= stripClientHints;
        return instance;
    }
    private ClientHintsRequests(CoreWebView2 core,Func<bool> current,Func<string,Task>? onFailure)
    {
        _core=core;_current=current;_onFailure=onFailure;
        core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused").DevToolsProtocolEventReceived += Paused;
        core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_,e) =>
        {
            if (!_sessions.Contains(e.SessionId)) return;
            using var document=JsonDocument.Parse(e.ParameterObjectAsJson);
            var id=document.RootElement.GetProperty("requestId").GetString();
            if (id is not null)
            {
                if (_failed.Count >= 8192) _failed.Clear();
                _failed.Add((e.SessionId,id));
            }
        };
    }
    public async Task ConfigureAsync(string? session=null)
    {
        if (_sessions.Contains(session ?? "")) return;
        _sessions.Add(session ?? "");
        await CallAsync(session,"Network.enable","{\"maxTotalBufferSize\":0,\"maxResourceBufferSize\":0,\"maxPostDataSize\":0}");
        await CallAsync(session,"Fetch.enable","{\"patterns\":[{\"urlPattern\":\"*\",\"requestStage\":\"Request\"}]}");
    }
    public void Forget(string session)
    {
        _sessions.Remove(session);
        _failed.RemoveWhere(p=>p.Session==session);
    }
    private Task<string> CallAsync(string? session,string method,string arguments) => string.IsNullOrEmpty(session)
        ? _core.CallDevToolsProtocolMethodAsync(method,arguments)
        : _core.CallDevToolsProtocolMethodForSessionAsync(session,method,arguments);
    private async void Paused(object? sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_sessions.Contains(e.SessionId) || !_current()) return;
        string? requestId=null,networkId=null;
        try
        {
            using var document=JsonDocument.Parse(e.ParameterObjectAsJson);
            var root=document.RootElement;
            requestId=root.GetProperty("requestId").GetString()!;
            if (root.TryGetProperty("networkId",out var network)) networkId=network.GetString();
            var request=root.GetProperty("request");
            var original=request.GetProperty("headers").EnumerateObject().ToArray();
            var diagnostic=request.GetProperty("method").GetString()=="GET" && FingerprintProbePage.IsPageUri(_core.Source)
                && original.Any(p=>p.Name.Equals("Origin",StringComparison.OrdinalIgnoreCase)&&p.Value.GetString()==$"https://{FingerprintProbePage.Host}");
            var requiresOrigin=Uri.TryCreate(request.GetProperty("url").GetString(),UriKind.Absolute,out var uri)
                && uri is {Scheme:"https",IsDefaultPort:true} && uri.Host is "api.ipify.org" or "api6.ipify.org";
            bool Remove(JsonProperty header) => _stripClientHints && StandardFingerprintPrivacy.IsClientHintHeader(header.Name)
                || diagnostic && (header.Name.Equals("Referer",StringComparison.OrdinalIgnoreCase)
                    || !requiresOrigin && header.Name.Equals("Origin",StringComparison.OrdinalIgnoreCase));
            // Do not log URLs, header values, cookies or authorization data.
            var arguments=original.Any(Remove)
                ? JsonSerializer.Serialize(new {requestId,headers=original.Where(p=>!Remove(p)).Select(p=>new {name=p.Name,value=p.Value.GetString()})})
                : JsonSerializer.Serialize(new {requestId});
            await CallAsync(e.SessionId,"Fetch.continueRequest",arguments).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch(Exception ex)
        {
            if (!_current() || !_sessions.Contains(e.SessionId)) return;
            // Pages routinely abort requests. An already-terminal request cannot
            // send unfiltered headers and must not cause a profile disconnect.
            if (networkId is not null && _failed.Remove((e.SessionId,networkId))) return;
            if (networkId is not null)
            {
                // Let an already-queued loadingFailed notification reach the UI
                // before treating a failed continuation as a live protocol error.
                await Task.Delay(25);
                if (!_current() || !_sessions.Contains(e.SessionId) || _failed.Remove((e.SessionId,networkId))) return;
            }
            if (requestId is not null)
                try { await CallAsync(e.SessionId,"Fetch.failRequest",JsonSerializer.Serialize(new {requestId,errorReason="BlockedByClient"})).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* Teardown or an already-completed request. */ }
            if (_onFailure is not null)
                try { await _onFailure("Не удалось ограничить HTTP Client Hints: " + ex.Message); }
                catch { /* Host teardown must not escape the async event handler. */ }
        }
    }
}
