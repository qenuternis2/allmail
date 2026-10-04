using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Privacy;

internal static class InternalPageHeadersSmoke
{
    public static async Task RunAsync(Window window,string root)
    {
        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"internal-headers"),new(){ExclusiveUserDataFolderAccess=true});
        var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
        try
        {
            var grid=new Grid();window.Content=grid;
            using var main=new WebView2();grid.Children.Add(main);
            await CheckAsync(main,environment,"main");
            using var child=new WebView2();grid.Children.Add(child);
            await CheckAsync(child,environment,"child");
            if(main.CoreWebView2.BrowserProcessId!=child.CoreWebView2.BrowserProcessId)throw new InvalidOperationException("Header child controller did not share environment.");
        }
        finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
    }
    private static async Task CheckAsync(WebView2 view,CoreWebView2Environment environment,string scope)
    {
        await view.EnsureCoreWebView2Async(environment);
        var core=view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("allmail-smoke.test",AppContext.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
        const string site="https://allmail-smoke.test/header-control.html";
        const string legacy="https://probe.protonprofiles.invalid";
        var internalOrigins=new[]{legacy,"https://diagnostics.invalid","https://contexts.invalid","https://ua-hints-bootstrap.protonprofiles.invalid"};
        foreach(var origin in internalOrigins)
            core.SetVirtualHostNameToFolderMapping(new Uri(origin).Host,AppContext.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
        async Task NavigateAsync(string url)
        {
            var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Complete(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>ready.TrySetResult(e.IsSuccess);
            core.NavigationCompleted+=Complete;
            try
            {
                core.Navigate(url);
                if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)))throw new InvalidOperationException("Real site header fixture navigation failed.");
            }
            finally {core.NavigationCompleted-=Complete;}
        }
        await NavigateAsync(legacy+"/header-control.html");
        using var server=new UaHintsServer();
        async Task<Dictionary<string,string>> ReceiveAsync(string method="GET")
        {
            var options=new Dictionary<string,object>{{"method",method},{"credentials","omit"},{"referrerPolicy","unsafe-url"}};
            if(method=="POST")options["body"]="fixture";
            using var response=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {
                expression=$"fetch({JsonSerializer.Serialize(server.Uri+"echo")},{JsonSerializer.Serialize(options)}).then(r=>{{if(!r.ok)throw new Error(r.status);return r.json();}})",awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(10)));
            if(response.RootElement.TryGetProperty("exceptionDetails",out var error))throw new InvalidOperationException("Real-site header receiver failed: "+error);
            return new Dictionary<string,string>(JsonSerializer.Deserialize<Dictionary<string,string>>(response.RootElement.GetProperty("result").GetProperty("value").GetRawText())!,StringComparer.OrdinalIgnoreCase);
        }
        var baseline=await ReceiveAsync();
        if(!baseline.TryGetValue("Origin",out var baselineOrigin)||baselineOrigin!=legacy
            || !baseline.TryGetValue("Referer",out var baselineReferer)||!InternalPageHeaders.IsInternalUri(baselineReferer))
            throw new InvalidOperationException("Legacy internal header positive control was unavailable.");
        string? failure=null;
        await ClientHintsRequests.ForCore(core,()=>true,reason=>{failure=reason;return Task.CompletedTask;},stripClientHints:false).ConfigureAsync();
        foreach(var origin in internalOrigins)
        {
            await NavigateAsync(origin+"/header-control.html");
            if(origin=="https://diagnostics.invalid")
                await core.ExecuteScriptAsync("history.replaceState({},'', '/fingerprint.html')");
            foreach(var method in new[]{"GET","POST"})
            {
                var received=await ReceiveAsync(method);
                if(received.Any(p=>p.Key.Equals("Origin",StringComparison.OrdinalIgnoreCase)&&InternalPageHeaders.IsInternalUri(p.Value)
                    || p.Key.Equals("Referer",StringComparison.OrdinalIgnoreCase)&&InternalPageHeaders.IsInternalUri(p.Value)))
                    throw new InvalidOperationException("Real site received a reserved internal Origin/Referer.");
                if(failure is not null)throw new InvalidOperationException(failure);
            }
        }
        await NavigateAsync(site);
        foreach(var method in new[]{"GET","POST"})
        {
            var received=await ReceiveAsync(method);
            if(!received.TryGetValue("Origin",out var origin)||origin!="https://allmail-smoke.test"
                || !received.TryGetValue("Referer",out var referer)||referer!=site)
                throw new InvalidOperationException("Legitimate site Origin/Referer changed.");
        }
        Console.WriteLine("PASS: real-site internal header guard "+scope+"; legacy Origin/Referer positive control; GET/POST internal addresses cleared; legitimate site Origin/Referer retained; browser CORS responses readable.");
    }
}
