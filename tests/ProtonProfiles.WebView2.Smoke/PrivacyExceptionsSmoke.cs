using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

internal static class PrivacyExceptionsSmoke
{
    private const string Observation="({audio:typeof AudioContext==='function',canvas:(()=>{const c=document.createElement('canvas');c.width=1;c.height=1;try{c.getContext('2d').getImageData(0,0,1,1);return true}catch{return false}})(),mediaDevices:typeof navigator.mediaDevices==='object',sharedWorker:typeof SharedWorker==='function',serviceWorker:typeof navigator.serviceWorker==='object',storageEstimate:typeof navigator.storage?.estimate==='function',mediaCapabilities:typeof navigator.mediaCapabilities==='object',webCodecs:typeof VideoDecoder==='function',keyboard:typeof navigator.keyboard==='object',battery:typeof navigator.getBattery==='function',gamepads:typeof navigator.getGamepads==='function',localFonts:typeof queryLocalFonts==='function'})";
    public static async Task RunAsync(Window window,string root)
    {
        foreach(var exceptions in new[]{PrivacyException.WebAudio,ProfilePrivacy.KnownExceptions})
        {
            var config=new ProfileConfig{Id=Guid.NewGuid(),DisplayName="Exception fixture",GraphicsPolicy=GraphicsPolicy.StrictFingerprintExperimental,PrivacyExceptions=exceptions,BrowserTimeZoneId="Europe/Riga"};
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"exceptions-"+(long)exceptions),new(){AdditionalBrowserArguments=BrowserArguments.Build(null,graphics:config.GraphicsPolicy,exceptions:exceptions),ExclusiveUserDataFolderAccess=true});
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
            try
            {
                var grid=new Grid();window.Content=grid;using var main=new WebView2();grid.Children.Add(main);await main.EnsureCoreWebView2Async(environment);
                var core=main.CoreWebView2;core.SetVirtualHostNameToFolderMapping("exception-smoke.test",AppContext.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
                await NavigateAsync(core,"https://exception-smoke.test/header-control.html");var baseline=await EvaluateAsync(core,Observation);
                string? failure=null;
                await UserAgentHintsBootstrap.ApplyAsync(core,config,onFailure:reason=>{failure=reason;return Task.CompletedTask;});
                if(AudioPageGuard.IsEnabled(config))await core.AddScriptToExecuteOnDocumentCreatedAsync(AudioPageGuard.Script);
                await UserAgentHintsBootstrap.VerifyAsync(core,environment,config,true);
                await NavigateAsync(core,"https://exception-smoke.test/header-control.html?protected");
                await CheckAsync(core,config,baseline,"main");
                using var child=new WebView2();grid.Children.Add(child);await child.EnsureCoreWebView2Async(environment);
                child.CoreWebView2.SetVirtualHostNameToFolderMapping("exception-smoke.test",AppContext.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
                await UserAgentHintsBootstrap.ApplyAsync(child.CoreWebView2,config,onFailure:reason=>{failure=reason;return Task.CompletedTask;});
                await NavigateAsync(child.CoreWebView2,"https://exception-smoke.test/header-control.html?child");await CheckAsync(child.CoreWebView2,config,baseline,"child");
                if(exceptions==ProfilePrivacy.KnownExceptions)
                {
                    const string workers="(async()=>{const inspect=()=>({cpu:navigator.hardwareConcurrency,timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,crypto:typeof crypto.subtle==='object',codecs:typeof VideoDecoder==='function'});const run=shared=>new Promise((resolve,reject)=>{const source=shared?'onconnect=e=>e.ports[0].postMessage(('+inspect.toString()+')())':'postMessage(('+inspect.toString()+')())';const url=URL.createObjectURL(new Blob([source],{type:'text/javascript'}));const w=shared?new SharedWorker(url):new Worker(url);const port=shared?w.port:w;port.onmessage=e=>{URL.revokeObjectURL(url);if(shared)port.close();else w.terminate();resolve(e.data)};w.onerror=()=>reject(new Error('worker failed'));if(shared)port.start();});return {dedicated:await run(false),shared:await run(true)};})()";
                    var workersResult=await EvaluateAsync(core,workers);
                    foreach(var scope in new[]{"dedicated","shared"})if(workersResult.GetProperty(scope).GetProperty("cpu").GetInt32()!=UserAgentHintsBootstrap.ExpectedCpu(core)||workersResult.GetProperty(scope).GetProperty("timeZone").GetString()!="Europe/Riga"||!workersResult.GetProperty(scope).GetProperty("crypto").GetBoolean()||!workersResult.GetProperty(scope).GetProperty("codecs").GetBoolean())throw new InvalidOperationException("Allowed worker startup mismatch "+workersResult);
                    // Serving a controlled service script does not enable external network or use account data.
                    const string sw="https://exception-smoke.test/exception-worker.js";core.AddWebResourceRequestedFilter(sw,CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
                    core.WebResourceRequested+=(_,e)=>{if(e.Request.Uri==sw)e.Response=environment.CreateWebResourceResponse(new MemoryStream("oninstall=e=>e.waitUntil(skipWaiting());onactivate=e=>e.waitUntil(clients.claim());"u8.ToArray()),200,"OK","Content-Type: text/javascript\r\nCache-Control: no-store\r\n");};
                    var service=await EvaluateAsync(core,"navigator.serviceWorker.register('/exception-worker.js').then(()=>navigator.serviceWorker.ready).then(r=>({active:!!r.active})).finally(async()=>{for(const r of await navigator.serviceWorker.getRegistrations())await r.unregister()})");
                    if(!service.GetProperty("active").GetBoolean())throw new InvalidOperationException("Allowed service worker did not start.");
                    Console.WriteLine("PASS: per-profile SharedWorker and service worker exceptions; startup succeeds, shared/dedicated native CPU/time zone retained; service script privacy coverage NotPerformed.");
                }
                if(failure is not null)throw new InvalidOperationException(failure);
                Console.WriteLine("PASS: native per-profile privacy exceptions "+(long)exceptions+" main/child; Web Crypto native SHA-256/AES-GCM/tamper rejection; remaining restrictions retained.");
            }
            finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        }
    }
    private static async Task CheckAsync(CoreWebView2 core,ProfileConfig config,JsonElement baseline,string scope)
    {
        var observed=await EvaluateAsync(core,Observation);
        if(!observed.GetProperty("audio").GetBoolean())throw new InvalidOperationException("Web Audio exception not applied.");
        foreach(var (key,feature) in new[]{("canvas",PrivacyException.CanvasReadback),("mediaDevices",PrivacyException.MediaDevices),("sharedWorker",PrivacyException.SharedWorkers),("serviceWorker",PrivacyException.ServiceWorkers),("storageEstimate",PrivacyException.StorageEstimate),("mediaCapabilities",PrivacyException.MediaCapabilities),("webCodecs",PrivacyException.WebCodecs),("keyboard",PrivacyException.KeyboardLayout),("battery",PrivacyException.Battery),("gamepads",PrivacyException.Gamepads),("localFonts",PrivacyException.LocalFonts)})
            if(observed.GetProperty(key).GetBoolean()!=(ProfilePrivacy.Allows(config,feature)&&baseline.GetProperty(key).GetBoolean()))throw new InvalidOperationException("Exception isolation failed "+scope+" "+key+" "+observed);
        var residual=await EvaluateAsync(core,ResidualFingerprintPrivacy.EvaluationScript);
        if(ResidualFingerprintPrivacy.ReadResult(residual.GetRawText(),config.PrivacyExceptions).Outcome!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Remaining residual guard failed "+residual);
        var crypto=(await EvaluateAsync(core,FingerprintProbePage.WebCryptoEvaluationScript)).GetProperty("webCrypto");
        foreach(var key in new[]{"secureContext","cryptoAvailable","subtleAvailable","nativeMethods","randomGeneration","sha256","aesGcmRoundTrip","aesGcmTamperRejected"})if(crypto.GetProperty(key).ValueKind!=JsonValueKind.True)throw new InvalidOperationException("Crypto failure "+scope+" "+crypto);
    }
    private static async Task<JsonElement> EvaluateAsync(CoreWebView2 core,string expression)
    {
        using var result=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new{expression,awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(12)));
        if(result.RootElement.TryGetProperty("exceptionDetails",out var error))throw new InvalidOperationException("Exception fixture evaluation failed: "+error);
        return result.RootElement.GetProperty("result").GetProperty("value").Clone();
    }
    private static async Task NavigateAsync(CoreWebView2 core,string uri)
    {
        var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);void Complete(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>ready.TrySetResult(e.IsSuccess);
        core.NavigationCompleted+=Complete;try{core.Navigate(uri);if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)))throw new InvalidOperationException("Exception fixture navigation failed.");}finally{core.NavigationCompleted-=Complete;}
    }
}
