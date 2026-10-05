using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;

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
    private bool _collectorConfigured;
    private ProxyEndpoint? _proxy;
    private Func<ProxyCredential?>? _credentials;
    private Action<string>? _authProblem;
    private readonly ProxyAuthRetryBudget _authBudget = new();
    private readonly Dictionary<(string Session,string NetworkId),HashSet<string>> _authRequests = [];
    public void ConfigureProxyAuthentication(ProxyEndpoint proxy, Func<ProxyCredential?> credentials, Action<string> problem)
    {
        if (_sessions.Count != 0) throw new InvalidOperationException("Proxy authentication must be configured before Fetch interception.");
        _proxy = proxy;
        _credentials = credentials;
        _authProblem = problem;
    }
    public void EnableCollector() => _collectorConfigured=true;
    public static ClientHintsRequests ForCore(CoreWebView2 core,Func<bool> current,Func<string,Task>? onFailure,bool stripClientHints=true)
    {
        var instance=Instances.GetValue(core,c=>new ClientHintsRequests(c,current,onFailure));
        instance._stripClientHints |= stripClientHints;
        return instance;
    }
    private ClientHintsRequests(CoreWebView2 core,Func<bool> current,Func<string,Task>? onFailure)
    {
        _core=core;
        _current=()=>{try{return current() && core.BrowserProcessId>0;}catch{return false;}};
        _onFailure=onFailure;
        core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused").DevToolsProtocolEventReceived += Paused;
        core.GetDevToolsProtocolEventReceiver("Fetch.authRequired").DevToolsProtocolEventReceived += Authenticate;
        core.GetDevToolsProtocolEventReceiver("Network.loadingFinished").DevToolsProtocolEventReceived += CompleteAuthentication;
        core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_,e) =>
        {
            CompleteAuthentication(_,e);
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
        await EnableFetchAsync(session);
    }
    // WebView2 rebuilds request factories when native resource filters are added
    // during bootstrap. Reinstall Fetch after those filters, before site traffic.
    public async Task RefreshAsync()
    {
        foreach(var session in _sessions.ToArray()) await EnableFetchAsync(session);
    }
    private Task<string> EnableFetchAsync(string? session) => CallAsync(session,"Fetch.enable",JsonSerializer.Serialize(new {
        patterns=new[]{new {urlPattern="*",requestStage="Request"}},handleAuthRequests=_proxy is not null}));
    public void Forget(string session)
    {
        _sessions.Remove(session);
        _failed.RemoveWhere(p=>p.Session==session);
        _authBudget.Forget(session);
        foreach(var key in _authRequests.Keys.Where(k=>k.Session==session).ToArray()) _authRequests.Remove(key);
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
            if (_proxy is not null && networkId is not null)
            {
                var key=(e.SessionId,networkId);
                if (!_authRequests.TryGetValue(key,out var requests)) _authRequests[key]=requests=[];
                requests.Add(requestId);
            }
            var request=root.GetProperty("request");
            var original=request.GetProperty("headers").EnumerateObject().ToArray();
            var collectorSource=_collectorConfigured && FingerprintProbePage.IsPageUri(_core.Source);
            var diagnostic=request.GetProperty("method").GetString()=="GET" && collectorSource
                && original.Any(p=>p.Name.Equals("Origin",StringComparison.OrdinalIgnoreCase)&&p.Value.GetString()==$"https://{FingerprintProbePage.Host}");
            var requiresOrigin=Uri.TryCreate(request.GetProperty("url").GetString(),UriKind.Absolute,out var uri)
                && uri is {Scheme:"https",IsDefaultPort:true} && uri.Host is "api.ipify.org" or "api6.ipify.org";
            var internalOrigin=original.Any(p=>p.Name.Equals("Origin",StringComparison.OrdinalIgnoreCase)
                && InternalPageHeaders.IsInternalUri(p.Value.GetString())) && !(diagnostic && requiresOrigin);
            var internalReferer=original.Any(p=>p.Name.Equals("Referer",StringComparison.OrdinalIgnoreCase)
                && InternalPageHeaders.IsInternalUri(p.Value.GetString()));
            // A missing Referer in an overridden header list can be regenerated by
            // Chromium from request referrer state. Explicitly clear that state only
            // for reserved internal addresses; ordinary site referrers stay intact.
            var clearReferer=internalReferer || internalOrigin && !collectorSource;
            bool Remove(JsonProperty header) => _stripClientHints && StandardFingerprintPrivacy.IsClientHintHeader(header.Name)
                || internalOrigin && header.Name.Equals("Origin",StringComparison.OrdinalIgnoreCase)
                || diagnostic && (header.Name.Equals("Referer",StringComparison.OrdinalIgnoreCase)
                    || !requiresOrigin && header.Name.Equals("Origin",StringComparison.OrdinalIgnoreCase));
            // Do not log URLs, header values, cookies or authorization data.
            var headers=original.Where(p=>!Remove(p) && !(clearReferer && p.Name.Equals("Referer",StringComparison.OrdinalIgnoreCase)))
                .Select(p=>new {name=p.Name,value=p.Value.GetString()}).ToList();
            if(clearReferer) headers.Add(new {name="Referer",value=(string?)""});
            var arguments=original.Any(Remove) || clearReferer
                ? JsonSerializer.Serialize(new {requestId,headers})
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

    private void CompleteAuthentication(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (_proxy is null || !_sessions.Contains(e.SessionId)) return;
        using var document=JsonDocument.Parse(e.ParameterObjectAsJson);
        var networkId=document.RootElement.GetProperty("requestId").GetString();
        if (networkId is not null && _authRequests.Remove((e.SessionId,networkId),out var requests))
            foreach(var requestId in requests) _authBudget.Complete(e.SessionId,requestId);
    }

    private async void Authenticate(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_sessions.Contains(e.SessionId)) return;
        string? requestId=null;
        try
        {
            using var document=JsonDocument.Parse(e.ParameterObjectAsJson);
            var root=document.RootElement;
            requestId=root.GetProperty("requestId").GetString()!;
            var challenge=root.GetProperty("authChallenge");
            ProxyCredential? credential=null;
            // Fetch distinguishes a website 401 from a proxy 407 and supplies a
            // stable request ID for rejected credentials. Independent connections,
            // GeoIP and tabs must not consume a generation-wide lifetime quota.
            if (_current() && challenge.TryGetProperty("source",out var source) && source.GetString()=="Proxy"
                && challenge.GetProperty("scheme").GetString()?.Equals("basic",StringComparison.OrdinalIgnoreCase)==true
                && ProxyChallengeMatcher.Evaluate(_proxy,challenge.GetProperty("origin").GetString(),true)
                    == ProxyChallengeMatcher.Decision.ReleaseProxyCredentials)
            {
                if (!_authBudget.TryConsume(e.SessionId,requestId))
                    _authProblem?.Invoke("Прокси отклонил учётные данные. Проверьте логин и пароль в настройках профиля.");
                else
                {
                    credential=_credentials?.Invoke();
                    if (credential is null) _authProblem?.Invoke("Учётные данные прокси не найдены.");
                }
            }
            object response=credential is null ? new {response="CancelAuth"}
                : new {response="ProvideCredentials",username=credential.UserName,password=credential.Password};
            await CallAsync(e.SessionId,"Fetch.continueWithAuth",JsonSerializer.Serialize(new {requestId,authChallengeResponse=response}))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            if (!_current() || !_sessions.Contains(e.SessionId)) return;
            // A navigation may abort while its auth response is in flight. A
            // failed cancel means the request is already terminal or disposed.
            if (requestId is not null)
                try { await CallAsync(e.SessionId,"Fetch.continueWithAuth",JsonSerializer.Serialize(new {
                    requestId,authChallengeResponse=new {response="CancelAuth"}})).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { }
        }
    }
}
