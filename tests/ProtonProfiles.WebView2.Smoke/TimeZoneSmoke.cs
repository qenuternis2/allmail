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
                    var audio=report.RootElement.GetProperty("htmlAudioDecode");
                    if(Math.Abs(audio.GetProperty("duration").GetDouble()-0.1)>0.001 || audio.GetProperty("readyState").GetInt32()<2 || !audio.GetProperty("nativeLoad").GetBoolean())
                        throw new InvalidOperationException("HTML audio decode failed after WebCodecs restriction: "+audio);
                    var tz=TimeZoneInfo.FindSystemTimeZoneById(zone);
                    foreach(var scope in new[]{"main","same","cross","worker"}) {
                        var value=report.RootElement.GetProperty(scope);
                        CheckWebCodecs(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        CheckKeyboardLayout(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        CheckDisplayDiscovery(value,policy==GraphicsPolicy.StrictFingerprintExperimental,scope);
                        if(value.GetProperty("timeZone").GetString()!=zone||!value.GetProperty("nativeDate").GetBoolean()||!value.GetProperty("nativeIntl").GetBoolean()
                            ||value.GetProperty("winter").GetInt32()!=-(int)tz.GetUtcOffset(new DateTimeOffset(2026,1,15,12,0,0,TimeSpan.Zero)).TotalMinutes
                            ||value.GetProperty("summer").GetInt32()!=-(int)tz.GetUtcOffset(new DateTimeOffset(2026,7,15,12,0,0,TimeSpan.Zero)).TotalMinutes)
                            throw new InvalidOperationException("Native timezone startup mismatch: "+scope+" "+value);
                    }
                    if(iframePrepared<1||failure is not null||core.Settings.UserAgent!=ua)throw new InvalidOperationException("Timezone setup missing OOP preparation or changed UA: "+failure);
                    await CheckTextInputAsync(core);
                    Console.WriteLine("PASS: Keyboard Layout startup "+policy+" "+label+"; first script in main/same/cross/dedicated; native KeyboardEvent and trusted browser text input/Enter retained: "+report.RootElement);
                    Console.WriteLine("PASS: native timezone startup "+policy+" "+label+"; OOP iframe preparation observed; main/same/cross/dedicated first script, winter/summer offsets, native Date/Intl and UA retained: "+report.RootElement);
                    Console.WriteLine("PASS: WebCodecs startup "+policy+" "+label+"; first script in main/same/cross/dedicated; native HTML media retained: "+report.RootElement);
                    Console.WriteLine("PASS: native display discovery startup "+policy+" "+label+"; first script in main/same/cross/dedicated; native HTML audio decode retained: "+report.RootElement);
                }
            }
            finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
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
    private static async Task CheckTextInputAsync(CoreWebView2 core)
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
    private static async Task<string> ObserveAsync(CoreWebView2 core)
    {
        var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender,CoreWebView2WebMessageReceivedEventArgs e){if(e.Source=="https://allmail-smoke.test/timezone.html")result.TrySetResult(e.TryGetWebMessageAsString());}
        core.WebMessageReceived+=Received;
        try {core.Navigate("https://allmail-smoke.test/timezone.html");return await result.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        finally {core.WebMessageReceived-=Received;}
    }
    private static async Task NavigateBlankAsync(CoreWebView2 core)
    {
        var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>result.TrySetResult(e.IsSuccess);
        core.NavigationCompleted+=Completed;
        try {core.Navigate("about:blank");if(!await result.Task.WaitAsync(TimeSpan.FromSeconds(10)))throw new InvalidOperationException("Timezone control cleanup failed.");}
        finally {core.NavigationCompleted-=Completed;}
    }
}
