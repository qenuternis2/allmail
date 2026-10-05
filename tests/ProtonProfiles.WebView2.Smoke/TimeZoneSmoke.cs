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

internal static class TimeZoneSmoke
{
    public static async Task RunAsync(Window window,string root)
    {
        foreach(var policy in new[]{GraphicsPolicy.RuntimeDefault,GraphicsPolicy.StrictFingerprintExperimental})
        {
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"timezone-"+policy),new(){
                AdditionalBrowserArguments=BrowserArguments.Build(null,graphics:policy)+" --site-per-process",ExclusiveUserDataFolderAccess=true});
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
            try
            {
                var grid=new Grid();window.Content=grid;
                using var main=new WebView2();using var child=new WebView2();grid.Children.Add(main);grid.Children.Add(child);
                string? zone=null;
                foreach(var (view,label) in new[]{(main,"main"),(child,"child")})
                {
                    // Frame callbacks require a visible compositor surface. Keep the other
                    // controller alive but hidden instead of overlapping the tested video.
                    main.Visibility=ReferenceEquals(view,main)?Visibility.Visible:Visibility.Hidden;
                    child.Visibility=ReferenceEquals(view,child)?Visibility.Visible:Visibility.Hidden;
                    await view.EnsureCoreWebView2Async(environment);var core=view.CoreWebView2;
                    foreach(var host in new[]{"allmail-smoke.test","allmail-frame.test"})
                        core.SetVirtualHostNameToFolderMapping(host,AppContext.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
                    if(zone is null) {
                        var native=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("Intl.DateTimeFormat().resolvedOptions().timeZone"));
                        zone=native=="Europe/Riga"?"America/New_York":"Europe/Riga";
                        await core.CallDevToolsProtocolMethodAsync("Emulation.setTimezoneOverride",JsonSerializer.Serialize(new{timezoneId=zone}));
                        using var control=JsonDocument.Parse(await ObserveAsync(core));
                        if(control.RootElement.GetProperty("main").GetProperty("timeZone").GetString()!=zone
                            ||control.RootElement.GetProperty("cross").GetProperty("timeZone").GetString()==zone)
                            throw new InvalidOperationException("Timezone root-only negative control did not reproduce the OOP iframe gap: "+control.RootElement);
                        Console.WriteLine("PASS: root-only timezone negative control; cross-origin first script retains host zone: "+control.RootElement);
                        foreach(var scope in new[]{"main","same","cross","worker"})CheckCanvasTextMetrics(control.RootElement.GetProperty(scope),false,scope);
                        Console.WriteLine("PASS: Canvas text metrics positive control; HTML/Offscreen measureText readable, text drawing retained: "+control.RootElement);
                        foreach(var scope in new[]{"main","same","cross","worker"})CheckWebCodecs(control.RootElement.GetProperty(scope),false,scope);
                        Console.WriteLine("PASS: WebCodecs positive control before production script; eight constructors available in main/same/cross/dedicated: "+control.RootElement);
                        if(policy==GraphicsPolicy.RuntimeDefault) {
                            foreach(var scope in new[]{"main","same","cross","worker"})CheckKeyboardLayout(control.RootElement.GetProperty(scope),false,scope);
                            Console.WriteLine("PASS: Keyboard Layout positive control before production script; keyboard entry points available in main/same/cross, naturally absent in dedicated worker: "+control.RootElement);
                            foreach(var scope in new[]{"main","same","cross","worker"})CheckDisplayDiscovery(control.RootElement.GetProperty(scope),false,scope);
                            Console.WriteLine("PASS: native display discovery positive control; Remote Playback and Presentation available in main/same/cross, naturally absent in dedicated worker: "+control.RootElement);
                        }
                        await NavigateBlankAsync(core);
                    }
                    var config=new ProfileConfig{Id=Guid.NewGuid(),DisplayName="timezone "+label,GraphicsPolicy=policy,BrowserTimeZoneId=zone};
                    var iframePrepared=0;string? failure=null;var ua=core.Settings.UserAgent;
                    await UserAgentHintsBootstrap.ApplyAsync(core,config,onFailure:reason=>{failure=reason;return Task.CompletedTask;},
                        diagnostic:message=>{if(message.StartsWith("Time zone target iframe:"))iframePrepared++;});
                    using var report=JsonDocument.Parse(await ObserveAsync(core));
                    if(policy==GraphicsPolicy.StrictFingerprintExperimental&&(!report.RootElement.GetProperty("animationFrame").GetProperty("aligned").GetBoolean()||!report.RootElement.GetProperty("intlFractionalAligned").GetBoolean()))throw new InvalidOperationException("Native RAF/Intl precise clock bypass: "+report.RootElement);
                    var audio=report.RootElement.GetProperty("htmlAudioDecode");
                    if(Math.Abs(audio.GetProperty("duration").GetDouble()-0.1)>0.001 || audio.GetProperty("readyState").GetInt32()<2 || !audio.GetProperty("nativeLoad").GetBoolean())
                        throw new InvalidOperationException("HTML audio decode failed after WebCodecs restriction: "+audio);
                    var video=report.RootElement.GetProperty("htmlVideoDecode");
                    var strict=policy==GraphicsPolicy.StrictFingerprintExperimental;
                    if(Math.Abs(video.GetProperty("duration").GetDouble()-0.4)>0.02||video.GetProperty("readyState").GetInt32()<2
                        ||video.GetProperty("width").GetInt32()!=32||video.GetProperty("height").GetInt32()!=32
                        ||!video.GetProperty("played").GetBoolean()||!video.GetProperty("nativePlay").GetBoolean()
                        ||(strict?video.GetProperty("callbackObserved").ValueKind!=JsonValueKind.Null
                            :video.GetProperty("callbackObserved").ValueKind!=JsonValueKind.True||video.GetProperty("quality").GetProperty("totalVideoFrames").GetInt32()<1))
                        throw new InvalidOperationException("Native HTML video playback/telemetry mismatch: "+video);
                    if(strict&&video.GetProperty("quality").ValueKind!=JsonValueKind.Null)throw new InvalidOperationException("Video quality stats exposed.");
                    var tz=TimeZoneInfo.FindSystemTimeZoneById(zone);
                    foreach(var scope in new[]{"main","same","cross","worker"}) {
                        var value=report.RootElement.GetProperty(scope);
                        CheckRemaining(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        CheckCanvasTextMetrics(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        CheckWebCodecs(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        CheckKeyboardLayout(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        CheckDisplayDiscovery(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        if(value.GetProperty("timeZone").GetString()!=zone||!value.GetProperty("nativeDate").GetBoolean()||!value.GetProperty("nativeIntl").GetBoolean()
                            ||value.GetProperty("winter").GetInt32()!=-(int)tz.GetUtcOffset(new DateTimeOffset(2026,1,15,12,0,0,TimeSpan.Zero)).TotalMinutes
                            ||value.GetProperty("summer").GetInt32()!=-(int)tz.GetUtcOffset(new DateTimeOffset(2026,7,15,12,0,0,TimeSpan.Zero)).TotalMinutes)
                            throw new InvalidOperationException("Native timezone startup mismatch: "+scope+" "+value);
                    }
                    if(iframePrepared<1||failure is not null||core.Settings.UserAgent!=ua)throw new InvalidOperationException("Timezone setup missing OOP preparation or changed UA: "+failure);
                    await PerformanceTimelineSmoke.CheckAsync(core,strict,policy+" "+label);
                    var textInput=await CheckTextInputAsync(core);
                    var keyboardProof=report.RootElement.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());
                    keyboardProof["nativeTextInput"]=textInput;
                    Console.WriteLine("PASS: Keyboard Layout startup "+policy+" "+label+"; first script in main/same/cross/dedicated; native KeyboardEvent and trusted browser text input/Enter retained: "+JsonSerializer.Serialize(keyboardProof));
                    Console.WriteLine("PASS: native timezone startup "+policy+" "+label+"; OOP iframe preparation observed; main/same/cross/dedicated first script, winter/summer offsets, native Date/Intl and UA retained: "+report.RootElement);
                    Console.WriteLine("PASS: remaining privacy startup "+policy+" "+label+"; first script main/same/forced-OOP/dedicated: "+report.RootElement);
                    Console.WriteLine("PASS: video telemetry startup "+policy+" "+label+"; first script main/same/forced-OOP/dedicated; marker-free clocks; real native VP8 playback retained: "+report.RootElement);
                    Console.WriteLine("PASS: Canvas text metrics startup "+policy+" "+label+"; first script in main/same/forced-OOP/dedicated; text drawing retained: "+report.RootElement);
                    Console.WriteLine("PASS: WebCodecs startup "+policy+" "+label+"; first script in main/same/cross/dedicated; native HTML media retained: "+report.RootElement);
                    Console.WriteLine("PASS: native display discovery startup "+policy+" "+label+"; first script in main/same/cross/dedicated; native HTML audio decode retained: "+report.RootElement);
                }
            }
            finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        }
    }
    private static void CheckRemaining(JsonElement observation,bool strict,string scope)
    {
        var o=observation.GetProperty("remaining");var clocks=o.GetProperty("clocks");
        if(clocks.GetProperty("quantumEvidence").GetBoolean()!=strict||clocks.GetProperty("symbolCounts").EnumerateArray().Any(v=>v.GetInt32()!=0))
            throw new InvalidOperationException("Clock behavioral evidence or exposed symbol mismatch: "+scope+" "+o);
        var telemetry=o.GetProperty("videoTelemetry");
        var keys=new[]{"requestVideoFrameCallback","cancelVideoFrameCallback","getVideoPlaybackQuality","webkitDecodedFrameCount","webkitDroppedFrameCount"};
        if(telemetry.GetProperty("status").GetString()!=(scope=="worker"?"NotApplicable":"Observed")
            ||keys.Any(k=>telemetry.GetProperty(k).ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new InvalidOperationException("Video telemetry observation incomplete: "+scope);
        if(strict||scope=="worker") {
            if(keys.Any(k=>telemetry.GetProperty(k).GetBoolean()))throw new InvalidOperationException("Video telemetry exposed: "+scope);
        }else if(keys.Take(3).Any(k=>!telemetry.GetProperty(k).GetBoolean()))throw new InvalidOperationException("Native video telemetry positive control missing: "+scope);
        var supplemental=observation.GetProperty("supplementalClockChecks").GetProperty("supplementalTimelineLocked");
        if(scope=="worker" ? supplemental.ValueKind==JsonValueKind.False : supplemental.GetBoolean()!=strict)throw new InvalidOperationException("Supplemental startup descriptors mismatch: "+scope+" "+supplemental);
        if(strict) {
            foreach(var key in new[]{"now","origin","dateNow","date","event","entry","serialized"})if(!clocks.GetProperty(key).GetBoolean())throw new InvalidOperationException("Unrounded native clock: "+scope+" "+key+" "+o);
            if(clocks.GetProperty("temporal").ValueKind==JsonValueKind.False)throw new InvalidOperationException("Temporal clock exposed: "+scope);
            if(MathImplementationPrivacy.ReadResult(o.GetProperty("mathPow").GetRawText())!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Reference Math.pow mismatch: "+scope+" "+o);
        }
        if(o.GetProperty("fontSetCheck").GetBoolean()==strict)throw new InvalidOperationException("FontFaceSet.check mismatch: "+scope+" "+o);
        var area=o.GetProperty("workArea");
        if(scope=="worker") {if(area.ValueKind!=JsonValueKind.Null)throw new InvalidOperationException("Worker gained screen.");}
        else if(strict&&(area.GetProperty("availWidth").GetInt32()!=area.GetProperty("width").GetInt32()||area.GetProperty("availHeight").GetInt32()!=area.GetProperty("height").GetInt32()||new[]{"availLeft","availTop","x","y","left","top"}.Any(k=>area.GetProperty(k).GetInt32()!=0)))throw new InvalidOperationException("Work area mismatch: "+scope+" "+o);
    }
    private static void CheckCanvasTextMetrics(JsonElement observation,bool blocked,string scope)
    {
        var metrics=observation.GetProperty("canvasTextMetrics");
        foreach(var kind in new[]{"html","offscreen"})
        {
            var value=metrics.GetProperty(kind);
            if(kind=="html"&&scope=="worker") {if(value.ValueKind!=JsonValueKind.Null)throw new InvalidOperationException("Worker gained HTML Canvas.");continue;}
            if(value.GetProperty("available").GetBoolean()==blocked||value.GetProperty("readable").GetBoolean()==blocked||!value.GetProperty("drawing").GetBoolean())
                throw new InvalidOperationException("Canvas text metrics mismatch: "+scope+" "+kind+" "+value);
        }
    }
    private static void CheckWebCodecs(JsonElement observation,bool blocked,string scope)
    {
        var codecs=observation.GetProperty("webCodecs");
        var names=new[]{"AudioDecoder","VideoDecoder","AudioEncoder","VideoEncoder","AudioData","VideoFrame","EncodedAudioChunk","EncodedVideoChunk"};
        if(codecs.EnumerateObject().Count()!=names.Length || names.Any(name=>codecs.GetProperty(name).GetBoolean()==blocked))
            throw new InvalidOperationException("WebCodecs startup mismatch: "+scope+" "+observation);
        if(scope=="worker") {
            if(observation.GetProperty("htmlAudioSupport").ValueKind!=JsonValueKind.Null)throw new InvalidOperationException("Worker gained HTML media.");
        } else if(observation.GetProperty("htmlAudioSupport").GetString() is not ("maybe" or "probably")
            ||!observation.GetProperty("nativeCanPlayType").GetBoolean())throw new InvalidOperationException("Native HTML media changed: "+scope);
    }
    private static void CheckKeyboardLayout(JsonElement observation,bool blocked,string scope)
    {
        var apis=observation.GetProperty("keyboardLayout");
        var names=new[]{"keyboard","Keyboard","KeyboardLayoutMap","getLayoutMap","lock","unlock"};
        if(apis.EnumerateObject().Count()!=names.Length || names.Any(name=>apis.GetProperty(name).GetBoolean()!=(scope!="worker"&&!blocked)))
            throw new InvalidOperationException("Keyboard Layout startup mismatch: "+scope+" "+apis);
        if(scope=="worker" ? observation.GetProperty("nativeKeyboardEvent").ValueKind!=JsonValueKind.Null : !observation.GetProperty("nativeKeyboardEvent").GetBoolean())
            throw new InvalidOperationException("Native keyboard event changed: "+scope);
    }
    private static async Task<JsonElement> CheckTextInputAsync(CoreWebView2 core)
    {
        await core.ExecuteScriptAsync("""
            (()=>{const input=document.createElement('input');input.id='native-keyboard-smoke';document.body.appendChild(input);
              globalThis.__keyboardInput={inputEvents:0,trustedInput:true,enterEvents:0,trustedEnter:true};
              input.oninput=e=>{__keyboardInput.inputEvents++;__keyboardInput.trustedInput&&=e.isTrusted;};
              input.onkeydown=e=>{if(e.key==='Enter'){__keyboardInput.enterEvents++;__keyboardInput.trustedEnter&&=e.isTrusted;}};
              input.focus();})()
            """);
        await core.CallDevToolsProtocolMethodAsync("Input.insertText",JsonSerializer.Serialize(new{text="All Mails"}));
        foreach(var type in new[]{"keyDown","keyUp"})
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new{type,key="Enter",code="Enter",windowsVirtualKeyCode=13}));
        using var input=JsonDocument.Parse(await core.ExecuteScriptAsync("({...__keyboardInput,value:document.getElementById('native-keyboard-smoke').value})"));
        var value=input.RootElement;
        if(value.GetProperty("value").GetString()!="All Mails" || value.GetProperty("inputEvents").GetInt32()<1 || !value.GetProperty("trustedInput").GetBoolean()
            || value.GetProperty("enterEvents").GetInt32()!=1 || !value.GetProperty("trustedEnter").GetBoolean())
            throw new InvalidOperationException("Trusted browser input or Enter failed: "+value);
        return value.Clone();
    }
    private static void CheckDisplayDiscovery(JsonElement observation,bool blocked,string scope)
    {
        var apis=observation.GetProperty("displayDiscovery");
        var names=new[]{"presentation","mediaRemote","RemotePlayback","Presentation","PresentationRequest","PresentationAvailability","PresentationConnection","PresentationConnectionAvailableEvent","PresentationConnectionCloseEvent","PresentationConnectionList","PresentationReceiver"};
        if(apis.EnumerateObject().Count()!=names.Length || names.Any(name=>apis.GetProperty(name).ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new InvalidOperationException("Display discovery startup incomplete: "+scope+" "+apis);
        if(blocked || scope=="worker") {
            if(names.Any(name=>apis.GetProperty(name).GetBoolean()))throw new InvalidOperationException("Display discovery exposed: "+scope+" "+apis);
        } else foreach(var name in new[]{"presentation","mediaRemote","RemotePlayback","PresentationRequest"})
            if(!apis.GetProperty(name).GetBoolean())throw new InvalidOperationException("Native display discovery positive control unavailable: "+scope+" "+apis);
    }
    internal static async Task<string> ObserveAsync(CoreWebView2 core)
    {
        var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender,CoreWebView2WebMessageReceivedEventArgs e){if(e.Source=="https://allmail-smoke.test/timezone.html")result.TrySetResult(e.TryGetWebMessageAsString());}
        core.WebMessageReceived+=Received;
        try {core.Navigate("https://allmail-smoke.test/timezone.html");return await result.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        finally {core.WebMessageReceived-=Received;}
    }
    internal static async Task NavigateBlankAsync(CoreWebView2 core)
    {
        var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId=null;
        void Starting(object? sender,CoreWebView2NavigationStartingEventArgs e)
        {if(e.Uri=="about:blank")navigationId=e.NavigationId;}
        void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e)
        {if(navigationId is not null&&e.NavigationId==navigationId)result.TrySetResult(e.IsSuccess&&core.Source=="about:blank");}
        core.NavigationStarting+=Starting;
        core.NavigationCompleted+=Completed;
        try {core.Navigate("about:blank");if(!await result.Task.WaitAsync(TimeSpan.FromSeconds(10)))throw new InvalidOperationException("Timezone control cleanup failed.");}
        finally {core.NavigationStarting-=Starting;core.NavigationCompleted-=Completed;}
    }
}
