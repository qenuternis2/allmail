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
    private const string Observation="({audio:typeof AudioContext==='function',canvas:(()=>{const c=document.createElement('canvas');c.width=1;c.height=1;try{c.getContext('2d').getImageData(0,0,1,1);return true}catch{return false}})(),canvasTextMetrics:(()=>{const c=document.createElement('canvas').getContext('2d');return typeof c.measureText==='function'&&c.measureText('All Mails').width>0})(),mediaDevices:typeof navigator.mediaDevices==='object',sharedWorker:typeof SharedWorker==='function',serviceWorker:typeof navigator.serviceWorker==='object',storageEstimate:typeof navigator.storage?.estimate==='function',mediaCapabilities:typeof navigator.mediaCapabilities==='object',webCodecs:typeof VideoDecoder==='function',keyboard:typeof navigator.keyboard==='object',battery:typeof navigator.getBattery==='function',gamepads:typeof navigator.getGamepads==='function',localFonts:typeof queryLocalFonts==='function'})";
    public static async Task RunAsync(Window window,string root)
    {
        foreach(var exceptions in new[]{PrivacyException.WebAudio,PrivacyException.WebAudio|PrivacyException.CanvasTextMetrics,PrivacyException.WebAudio|PrivacyException.HighResolutionTimers|PrivacyException.ScreenWorkArea|PrivacyException.NativeMath,ProfilePrivacy.KnownExceptions})
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
                if(exceptions==PrivacyException.WebAudio)
                {
                    var closing=await EvaluateAsync(core,"Promise.all(Array.from({length:6},()=>new Promise((resolve,reject)=>{const url=URL.createObjectURL(new Blob([\"postMessage({objectConstructor:Object.prototype.constructor===Object,codecsBlocked:typeof VideoDecoder==='undefined',metricsBlocked:typeof new OffscreenCanvas(4,4).getContext('2d').measureText==='undefined',scriptRestricted:typeof navigator.getBattery==='undefined'&&navigator.deviceMemory===8});close();\"],{type:'text/javascript'}));const worker=new Worker(url);worker.onmessage=e=>{URL.revokeObjectURL(url);resolve(e.data)};worker.onerror=()=>reject(new Error('closing worker failed'));})))");
                    foreach(var item in closing.EnumerateArray())if(!item.GetProperty("objectConstructor").GetBoolean()||!item.GetProperty("codecsBlocked").GetBoolean()||!item.GetProperty("scriptRestricted").GetBoolean()||!item.GetProperty("metricsBlocked").GetBoolean())throw new InvalidOperationException("Closing worker ran without restrictions.");
                    await Task.Delay(250);
                    if(failure is not null)throw new InvalidOperationException(failure);
                    Console.WriteLine("PASS: six immediately closing workers execute with intact Object constructor and installed restrictions; no stale target failure.");
                }
                if(exceptions==ProfilePrivacy.KnownExceptions)
                {
                    var workers="(async()=>{const hintsScript="+JsonSerializer.Serialize(UserAgentHintsPrivacy.ObservationScript)+";const inspect=async()=>({cpu:navigator.hardwareConcurrency,timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,crypto:typeof crypto.subtle==='object',codecs:typeof VideoDecoder==='function',hints:await collectUaHintsObservation()});const run=shared=>new Promise((resolve,reject)=>{const source=hintsScript+(shared?'onconnect=async e=>e.ports[0].postMessage(await ('+inspect.toString()+')())':'(async()=>postMessage(await ('+inspect.toString()+')()))()');const url=URL.createObjectURL(new Blob([source],{type:'text/javascript'}));const w=shared?new SharedWorker(url):new Worker(url);const port=shared?w.port:w;port.onmessage=e=>{URL.revokeObjectURL(url);if(shared)port.close();else w.terminate();resolve(e.data)};w.onerror=()=>reject(new Error('worker failed'));if(shared)port.start();});return {dedicated:await run(false),shared:await run(true)};})()";
                    var workersResult=await EvaluateAsync(core,workers);
                    var dedicated=workersResult.GetProperty("dedicated");var shared=workersResult.GetProperty("shared");
                    if(dedicated.GetProperty("cpu").GetInt32()!=UserAgentHintsBootstrap.ExpectedCpu(core)||dedicated.GetProperty("timeZone").GetString()!="Europe/Riga"||!dedicated.GetProperty("crypto").GetBoolean()||!dedicated.GetProperty("codecs").GetBoolean())throw new InvalidOperationException("Allowed dedicated worker startup mismatch "+workersResult);
                    if(!shared.GetProperty("crypto").GetBoolean()||shared.GetProperty("cpu").GetInt32()<1||string.IsNullOrWhiteSpace(shared.GetProperty("timeZone").GetString()))throw new InvalidOperationException("Allowed SharedWorker did not run.");
                    var sharedIdentity=UserAgentHintsPrivacy.ReadResult(shared.GetProperty("hints").GetRawText(),core.Settings.UserAgent,true).Outcome;
                    if(sharedIdentity==GraphicsReadbackOutcome.Unavailable)throw new InvalidOperationException("SharedWorker identity observation incomplete.");
                    Console.WriteLine("Allowed SharedWorker observation; privacy coverage NotPerformed; identity="+sharedIdentity+"; "+shared.GetRawText());
                    // Service-worker scripts must use a real HTTP receiver: WebView2 virtual
                    // folder mapping cannot supply this script-fetch path on the tested Runtime.
                    using var serviceServer=new UaHintsServer();var blank=serviceServer.Uri+"exception-blank.html";
                    core.AddWebResourceRequestedFilter(blank,CoreWebView2WebResourceContext.Document,CoreWebView2WebResourceRequestSourceKinds.All);
                    core.WebResourceRequested+=(_,e)=>{if(e.Request.Uri==blank)e.Response=environment.CreateWebResourceResponse(new MemoryStream("<!doctype html><body>Service-worker fixture</body>"u8.ToArray()),200,"OK","Content-Type: text/html\r\nCache-Control: no-store\r\n");};
                    await NavigateAsync(core,blank);
                    if(UserAgentHintsPrivacy.ReadResult(dedicated.GetProperty("hints").GetRawText(),core.Settings.UserAgent,true).Outcome!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Allowed worker leaked identity "+workersResult);
                    var service=await EvaluateAsync(core,"navigator.serviceWorker.register('/service.js').then(()=>navigator.serviceWorker.ready).then(r=>({active:!!r.active})).finally(async()=>{for(const r of await navigator.serviceWorker.getRegistrations())await r.unregister()})");
                    if(!service.GetProperty("active").GetBoolean())throw new InvalidOperationException("Allowed service worker did not start.");
                    Console.WriteLine("PASS: per-profile SharedWorker and service worker exceptions; startup succeeds, dedicated native identity/CPU/time zone retained; SharedWorker identity/script and service script privacy coverage NotPerformed.");
                }
                if(failure is not null)throw new InvalidOperationException(failure);
                if(Environment.GetEnvironmentVariable("ALLMAIL_PROTON_LIVE_CHECK")=="1")await ObservePublicProtonAsync(core,config);
                Console.WriteLine("PASS: native per-profile privacy exceptions "+(long)exceptions+" main/child; Web Crypto native SHA-256/AES-GCM/tamper rejection; remaining restrictions retained.");
            }
            finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        }
    }
    private static async Task ObservePublicProtonAsync(CoreWebView2 core,ProfileConfig config)
    {
        // Fresh disposable profiles, public landing page only; no account, form, media or permission use.
        core.PermissionRequested+=(_,e)=>{e.Handled=true;e.SavesInProfile=false;e.State=CoreWebView2PermissionState.Deny;};
        try
        {
            await NavigateAsync(core,"https://mail.proton.me/");
            JsonElement snapshot=default;
            for(var attempt=0;attempt<8;attempt++)
            {
                await Task.Delay(1000);
                snapshot=await EvaluateAsync(core,FingerprintProbePage.WebCryptoEvaluationScript);
                if(snapshot.GetProperty("protonSupportedBrowser").ValueKind==JsonValueKind.Number)break;
            }
            Console.WriteLine("Proton public compatibility observation: "+JsonSerializer.Serialize(new {exceptions=ProfilePrivacy.Names(config.PrivacyExceptions),status="Observed",snapshot}));
        }
        catch(Exception e)
        {
            // An external service/network outage cannot replace the mandatory controlled regression tests.
            Console.WriteLine("Proton public compatibility observation: "+JsonSerializer.Serialize(new {exceptions=ProfilePrivacy.Names(config.PrivacyExceptions),status="NotPerformed",errorName=e.GetType().Name}));
        }
    }
    private static async Task CheckAsync(CoreWebView2 core,ProfileConfig config,JsonElement baseline,string scope)
    {
        var observed=await EvaluateAsync(core,Observation);
        if(!observed.GetProperty("audio").GetBoolean())throw new InvalidOperationException("Web Audio exception not applied.");
        foreach(var (key,feature) in new[]{("canvas",PrivacyException.CanvasReadback),("canvasTextMetrics",PrivacyException.CanvasTextMetrics),("mediaDevices",PrivacyException.MediaDevices),("sharedWorker",PrivacyException.SharedWorkers),("serviceWorker",PrivacyException.ServiceWorkers),("storageEstimate",PrivacyException.StorageEstimate),("mediaCapabilities",PrivacyException.MediaCapabilities),("webCodecs",PrivacyException.WebCodecs),("keyboard",PrivacyException.KeyboardLayout),("battery",PrivacyException.Battery),("gamepads",PrivacyException.Gamepads),("localFonts",PrivacyException.LocalFonts)})
            if(observed.GetProperty(key).GetBoolean()!=(ProfilePrivacy.Allows(config,feature)&&baseline.GetProperty(key).GetBoolean()))throw new InvalidOperationException("Exception isolation failed "+scope+" "+key+" "+observed);
        var residual=await EvaluateAsync(core,ResidualFingerprintPrivacy.EvaluationScript);
        if(ResidualFingerprintPrivacy.ReadResult(residual.GetRawText(),config.PrivacyExceptions).Outcome!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Remaining residual guard failed "+residual);
        var value=residual.GetProperty("coarseClocks");
        var wrapped=value.GetProperty("quantumMs").ValueKind==JsonValueKind.Number;
        if(wrapped==ProfilePrivacy.Allows(config,PrivacyException.HighResolutionTimers))throw new InvalidOperationException("Timing exception mismatch: "+value);
        var math=await EvaluateAsync(core,MathImplementationPrivacy.EvaluationScript);
        if(!ProfilePrivacy.Allows(config,PrivacyException.NativeMath)&&MathImplementationPrivacy.ReadResult(math.GetRawText())!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Math exception guard failed: "+math);
        Console.WriteLine("Privacy remaining exceptions "+(long)config.PrivacyExceptions+" "+scope+": "+residual.GetRawText());
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
