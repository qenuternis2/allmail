using System.IO;
using System.Text.Json;
using System.Windows;
using ProtonProfiles.App.Browser;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--host-crash-fixture")
            return HostCrashSmoke.RunChild(args[1], Guid.Parse(args[2]));
        if (args.Length == 3 && args[0] == "--host-recovery-fixture")
            return HostCrashSmoke.RunChild(args[1], Guid.Parse(args[2]), delayExit: true);
        var exitCode = 1;
        var root = Path.Combine(Path.GetTempPath(), "sb-" + Guid.NewGuid().ToString("N"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ProtonProfiles.WebView2.Smoke;component/Themes/Modern.xaml", UriKind.Relative) });
        var window = new Window { Width = 400, Height = 300, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Loaded += async (_, _) =>
        {
            try
            {
                var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                Console.WriteLine("WebView2 Runtime: " + runtimeVersion);
                if (int.TryParse(Environment.GetEnvironmentVariable("ALLMAIL_MIN_WEBVIEW2_MAJOR"), out var minimumMajor)
                    && (!Version.TryParse(runtimeVersion, out var version) || version.Major < minimumMajor))
                    throw new InvalidOperationException("Native regression requires WebView2 major >= " + minimumMajor + "; observed " + runtimeVersion);
                if(Environment.GetEnvironmentVariable("ALLMAIL_PROTON_DIAGNOSTICS_ONLY")=="1") {await ProtonCompatibilityDiagnostics.RunAsync(window,root);exitCode=0;return;}
                if(Environment.GetEnvironmentVariable("ALLMAIL_CLOUDFLARE_DIAGNOSTICS_ONLY")=="1") {await CloudflareCompatibilityDiagnostics.RunAsync(window,root);exitCode=0;return;}
                if (Environment.GetEnvironmentVariable("ALLMAIL_DPI_ONLY") == "1")
                { await DpiSmoke.RunAsync(root, runtimeVersion).WaitAsync(TimeSpan.FromSeconds(90)); exitCode = 0; return; }
                SecurityAuditSmoke.Run();
                await ModernUiSmoke.RunAsync(root, runtimeVersion).WaitAsync(TimeSpan.FromSeconds(60));
                await ImportSettingsSmoke.RunAsync(root, runtimeVersion).WaitAsync(TimeSpan.FromSeconds(30));
                WindowsCredentialSmoke.Run(root);
                await HostCrashSmoke.RunAsync(root, runtimeVersion).WaitAsync(TimeSpan.FromSeconds(60));
                await HostCrashSmoke.RunAsync(root, runtimeVersion, delayExit: true).WaitAsync(TimeSpan.FromSeconds(90));
                await DownloadsSmoke.RunAsync(window, root).WaitAsync(TimeSpan.FromSeconds(120));
                await MathImplementationSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(45));
                await BootstrapNavigationSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(45));
                await ProfileTabsSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(150));
                await PermissionRequestsSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(45));
                // Observe the old behavior, without requiring GPU availability on the CI machine.
                await RunAsync(window, root, "legacy", "--disable-webgl --disable-features=WebGPU,WebGPUService --enable-blink-features=" + AdditionalFingerprintPrivacy.BlinkFeatures + ",UnrestrictedMeasureUserAgentSpecificMemory --force-effective-connection-type=Slow-2G", enforce: false)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                await RunAsync(window, root, "dpi-control", "--force-device-scale-factor=2", enforce: false, expectedScale: 2)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                await RunAsync(window, root, "restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockWebGlAndWebGpuExperimental), enforce: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native WebView2 graphics restriction; main, child and dedicated worker; CPU Canvas 2D works.");
                await RunAsync(window, root, "canvas-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockWebGlWebGpuAndCanvasReadbackExperimental), enforce: true, blockCanvas: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native Canvas readback restriction; main/child/dedicated worker; drawing commands accepted, pixel/blob exports blocked.");
                await RunAsync(window, root, "audio-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: document Web Audio restriction; main/child/loaded same-origin, srcdoc and cross-origin frames; worker APIs naturally absent; HTML media APIs retained; WebRTC Allow control passed.");
                await RunAsync(window, root, "dpr-normalized", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native DPR normalization; main/child, loaded frames, initial iframe and CSS media queries; responsive viewport, zoom control and native methods retained.");
                await RunAsync(window, root, "speech-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native Speech Synthesis restriction; main/child/loaded same-origin, srcdoc, cross-origin and initial iframe; baseline voice enumeration usable; worker APIs naturally absent; HTML Audio retained.");
                await RunAsync(window, root, "ua-hints-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true, blockUaHints: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                await RunAsync(window, root, "font-access-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true, blockUaHints: true, blockFontAccess: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native Local Font Access restriction; main/child/loaded same-origin, srcdoc, cross-origin and initial iframe; baseline APIs available; CSS font measurement retained; worker APIs naturally absent.");
                await RunAsync(window, root, "cpu-normalized", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true, blockUaHints: true, blockFontAccess: true, normalizeCpu: true)
                    .WaitAsync(TimeSpan.FromSeconds(90));
                Console.WriteLine("PASS: native CPU count normalization; forced 13 to 8 control; main/child, loaded and initial frames, dedicated/service worker startup; native getters retained; previous restrictions pass.");
                await RunAsync(window, root, "devices-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true, blockUaHints: true, blockFontAccess: true, normalizeCpu: true, blockDevices: true)
                    .WaitAsync(TimeSpan.FromSeconds(90));
                Console.WriteLine("PASS: native hardware devices restriction; baseline API availability; main/child, loaded and initial frames, dedicated/service worker startup; navigator entry points and constructors absent; previous restrictions pass.");
                await RunAsync(window, root, "pressure-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true, blockUaHints: true, blockFontAccess: true, normalizeCpu: true, blockDevices: true, blockPressure: true)
                    .WaitAsync(TimeSpan.FromSeconds(90));
                Console.WriteLine("PASS: native Compute Pressure restriction; baseline observer/record availability; main/child, loaded and initial frames, dedicated worker startup; service worker natural absence; previous restrictions pass; no CPU measurements performed.");
                await RunAsync(window, root, "strict-fingerprint", BrowserArguments.Build(null, graphics: GraphicsPolicy.StrictFingerprintExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true, blockUaHints: true, blockFontAccess: true, normalizeCpu: true, blockDevices: true, blockPressure: true, blockExtras: true)
                    .WaitAsync(TimeSpan.FromSeconds(90));
                await PrivacyExceptionsSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(90));
                await InternalPageHeadersSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(60));
                await GeoIpTimeZoneSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(70));
                await GeoIpTimeZoneSmoke.RunAsync(window,root, legacy: true).WaitAsync(TimeSpan.FromSeconds(70));
                await TimeZoneSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(90));
                await ProxyRoutingSmoke.RunAsync(window,root).WaitAsync(TimeSpan.FromSeconds(180));
                Console.WriteLine("PASS: native additional fingerprint restrictions; WebXR/display capture/audio output/extra sensors/NFC entry points absent; CPU performance and memory measurement APIs absent; global hardware permissions denied; NQE fixed 4G estimates in main/child, loaded/initial frames and dedicated worker startup; strict service worker targets stopped and cached workers bypassed; baseline forced feature and Slow-2G positive controls; previous restrictions pass.");
                await RejectCustomUaAsync(window,root)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native UA Client Hints restriction; native UA preserved; custom UA rejected before navigation; production secure bootstrap; main/child/loaded frames/dedicated/service workers; SharedWorker natively unavailable; actual loopback HTTP receiver with Accept-CH; previous privacy checks retained.");
                // Display changes and large DPI previews must not precede the
                // reference directory benchmark. Keep its initial desktop intact.
                await DpiSmoke.RunAsync(root, runtimeVersion).WaitAsync(TimeSpan.FromSeconds(90));
                exitCode = 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); }
            finally
            {
                app.Shutdown();
                try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        };
        app.Run(window);
        return exitCode;
    }

    private static async Task RejectCustomUaAsync(Window window,string root)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root,"ua-hints-custom-rejected"),new()
        {
            AdditionalBrowserArguments=BrowserArguments.Build(null,graphics:GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental),
            ExclusiveUserDataFolderAccess=true,
        });
        var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
        try
        {
            var grid=new Grid();window.Content=grid;
            using var view=new WebView2();
            grid.Children.Add(view);
            await view.EnsureCoreWebView2Async(environment);
            var core=view.CoreWebView2;
            var ua=core.Settings.UserAgent;var source=core.Source;
            var config=new ProfileConfig{Id=Guid.NewGuid(),DisplayName="unsupported custom UA",GraphicsPolicy=GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental,
                UserAgentMode=UserAgentMode.Custom,CustomUserAgent="allmail-smoke/1.0"};
            try {await UserAgentHintsBootstrap.ApplyAsync(core,config);throw new InvalidOperationException("Unsupported custom UA was accepted.");}
            catch (ArgumentException e) when (e.Message==UserAgentHintsPrivacy.CustomUserAgentError) { }
            if(core.Source!=source || core.Settings.UserAgent!=ua)throw new InvalidOperationException("Rejected UA combination changed browser state.");
            Console.WriteLine("PASS: unsupported custom UA rejected before navigation or UA mutation.");
        }
        finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
    }

    private static async Task RunAsync(Window window, string root, string label, string arguments, bool enforce, bool blockCanvas = false, bool blockAudio = false, bool allowRtc = false, bool normalizeDpr = false, double expectedScale = 1, bool blockSpeech = false, bool blockUaHints = false, string? customUa = null, bool blockFontAccess = false, bool normalizeCpu = false, bool blockDevices = false, bool blockPressure = false, bool blockExtras = false)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, label), new()
        {
            AdditionalBrowserArguments = arguments, ExclusiveUserDataFolderAccess = true,
            AllowSingleSignOnUsingOSPrimaryAccount = false, AreBrowserExtensionsEnabled = false,
        });
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        try
        {
            var grid = new Grid();
            window.Content = grid;
            using var main = new WebView2();
            grid.Children.Add(main);
            await CheckViewAsync(main, environment, label + " main", enforce, blockCanvas, blockAudio, allowRtc, normalizeDpr, 1, expectedScale, blockSpeech, blockUaHints, customUa, blockFontAccess, normalizeCpu, blockDevices, blockPressure, blockExtras);
            if (enforce)
            {
                using var child = new WebView2();
                grid.Children.Add(child);
                await CheckViewAsync(child, environment, label + " child", enforce, blockCanvas, blockAudio, allowRtc, normalizeDpr, normalizeDpr ? 1.25 : 1, expectedScale, blockSpeech, blockUaHints, customUa, blockFontAccess, normalizeCpu, blockDevices, blockPressure, blockExtras);
                if (main.CoreWebView2.BrowserProcessId != child.CoreWebView2.BrowserProcessId)
                    throw new InvalidOperationException("Child did not share the browser environment.");
            }
        }
        finally
        {
            window.Content = null;
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task CheckViewAsync(WebView2 view, CoreWebView2Environment environment, string label, bool enforce, bool blockCanvas, bool blockAudio, bool allowRtc, bool normalizeDpr, double zoom, double expectedScale, bool blockSpeech, bool blockUaHints, string? customUa, bool blockFontAccess, bool normalizeCpu, bool blockDevices, bool blockPressure, bool blockExtras)
    {
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        var config = new ProfileConfig {Id = Guid.NewGuid(), DisplayName = label, GraphicsPolicy = blockExtras ? GraphicsPolicy.StrictFingerprintExperimental : blockPressure ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental : blockDevices ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental : normalizeCpu ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental : blockFontAccess ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental : blockUaHints ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental : GraphicsPolicy.RuntimeDefault,
            UserAgentMode = customUa is null ? UserAgentMode.Default : UserAgentMode.Custom, CustomUserAgent = customUa, BrowserTimeZoneId = "Europe/Riga"};
        if (normalizeCpu)
        {
            // A non-bucket native count proves that the production normalization changes it,
            // even when the CI machine naturally reports a count already in a privacy bucket.
            await core.CallDevToolsProtocolMethodAsync("Emulation.setHardwareConcurrencyOverride","{\"hardwareConcurrency\":13}");
            var control=await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate","{\"expression\":\"navigator.hardwareConcurrency\",\"returnByValue\":true}");
            if (HardwareConcurrencyPrivacy.ReadNativeCdpCount(control)!=13) throw new InvalidOperationException("Native CPU positive control failed.");
        }
        var expectedUa = blockUaHints ? UserAgentHintsPrivacy.UserAgentToApply(config,core.Settings.UserAgent) : customUa ?? core.Settings.UserAgent;
        var protocolFailure = "";
        var serviceStopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (blockExtras)
        {
            // RAM and mediaDevices are secure-context APIs, naturally absent on about:blank.
            const string controlUri="https://residual-control.protonprofiles.invalid/";
            core.AddWebResourceRequestedFilter(controlUri+"*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested+=(_,e)=>{
                if(!e.Request.Uri.StartsWith(controlUri,StringComparison.Ordinal))return;
                var script=e.Request.Uri==controlUri+"cached.js";
                var bytes=script?"oninstall=e=>e.waitUntil(skipWaiting());onactivate=e=>e.waitUntil(clients.claim());onfetch=e=>{if(new URL(e.request.url).pathname==='/cache-proof')e.respondWith(new Response('cached-worker-response'));};"u8.ToArray():"<!doctype html><meta name=text-scale content=scale><body>original-host-response</body>"u8.ToArray();
                e.Response=environment.CreateWebResourceResponse(new MemoryStream(bytes),200,"OK","Content-Type: "+(script?"text/javascript":"text/html")+"\r\nCache-Control: no-store\r\n");
            };
            await NavigateAsync(core,controlUri);
            var cached=await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {expression="(async()=>{await navigator.serviceWorker.register('/cached.js');await navigator.serviceWorker.ready;if(!navigator.serviceWorker.controller)await new Promise(r=>navigator.serviceWorker.addEventListener('controllerchange',r,{once:true}));return await fetch('/cache-proof').then(r=>r.text());})()",awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(10));
            using(var c=JsonDocument.Parse(cached))
                if(c.RootElement.GetProperty("result").GetProperty("value").GetString()!="cached-worker-response")throw new InvalidOperationException("Existing service worker positive control failed: "+cached);
            Console.WriteLine(label+" cached service worker positive control: response intercepted");
            var residualControl=await core.ExecuteScriptAsync("({screen:[screen.width,screen.height],secure:isSecureContext,ram:navigator.deviceMemory,battery:typeof navigator.getBattery,gamepads:typeof navigator.getGamepads,media:typeof navigator.mediaDevices})");
            Console.WriteLine(label+" screen/RAM/device positive control: "+residualControl);
            if(await core.ExecuteScriptAsync("isSecureContext && navigator.deviceMemory>0 && typeof navigator.getGamepads==='function' && typeof navigator.mediaDevices==='object'")!="true")
                throw new InvalidOperationException("Screen/RAM/device positive control unavailable.");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-color-scheme\",\"value\":\"dark\"},{\"name\":\"prefers-contrast\",\"value\":\"more\"},{\"name\":\"color-gamut\",\"value\":\"p3\"}]}");
            await core.CallDevToolsProtocolMethodAsync("Page.setFontFamilies", "{\"fontFamilies\":{\"serif\":\"Arial\",\"sansSerif\":\"Times New Roman\",\"fixed\":\"Arial\"}}");
            await core.CallDevToolsProtocolMethodAsync("Page.setFontSizes", "{\"fontSizes\":{\"standard\":20,\"fixed\":18}}");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedOSTextScale", "{\"scale\":2}");
            await core.ExecuteScriptAsync("document.head.insertAdjacentHTML('beforeend','<meta name=text-scale content=scale>')");
            var baselineCdp = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {expression=StandardFingerprintPrivacy.EvaluationScript,awaitPromise=true,returnByValue=true}));
            using var baselineResult=JsonDocument.Parse(baselineCdp);
            if (baselineResult.RootElement.TryGetProperty("exceptionDetails",out _)) throw new InvalidOperationException("Native defaults positive control script failed: " + baselineCdp);
            var baseline=baselineResult.RootElement.GetProperty("result").GetProperty("value").GetRawText();
            Console.WriteLine(label + " native document defaults positive control: " + baseline);
            using var control=JsonDocument.Parse(baseline);
            if (!control.RootElement.GetProperty("localFontLoad").GetBoolean()
                || control.RootElement.GetProperty("media").GetProperty("prefers-color-scheme").GetBoolean()
                || control.RootElement.GetProperty("media").GetProperty("color-gamut").GetBoolean()
                || control.RootElement.GetProperty("genericFonts").GetProperty("serif").GetBoolean()
                || control.RootElement.GetProperty("defaultFontSize").GetDouble() <= 16
                || control.RootElement.GetProperty("osTextScale").GetDouble() <= 1
                || !control.RootElement.GetProperty("localFontRendering").GetBoolean())
                throw new InvalidOperationException("Native font source/media/generic fonts positive control failed.");
            // Chromium permits setFontFamilies only once per Page agent state.
            // Clear the fixture's agent record before production setup, leaving
            // the altered Settings in place for the normalization to actually change.
            await core.CallDevToolsProtocolMethodAsync("Page.disable", "{}");
        }
        await UserAgentHintsBootstrap.ApplyAsync(core, config, onFailure: reason => { protocolFailure = reason; Console.Error.WriteLine(reason); return Task.CompletedTask; }, diagnostic: message => {
            Console.WriteLine(label + " " + message);
            if(message=="Strict service worker target: stopped; startup not resumed")serviceStopped.TrySetResult();
        });
        if(blockExtras) {
            await serviceStopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var bypass=await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {expression="fetch('/cache-proof').then(r=>r.text())",awaitPromise=true,returnByValue=true}));
            using var c=JsonDocument.Parse(bypass);
            if(!c.RootElement.GetProperty("result").GetProperty("value").GetString()!.Contains("original-host-response",StringComparison.Ordinal))throw new InvalidOperationException("Stored service worker bypass failed: "+bypass);
            Console.WriteLine(label+" PASS: existing service worker stopped; native network bypass returns original host response; registrations retained.");
        }
        await UserAgentHintsBootstrap.VerifyAsync(core, environment, config, verify: true, diagnostic:json=>Console.WriteLine(label + " secure UA hints bootstrap: " + json));
        using var hintsServer = blockUaHints || label.StartsWith("legacy ", StringComparison.Ordinal) ? new UaHintsServer() : null;

        view.ZoomFactor = zoom;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ScreenPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(SpeechPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(UserAgentHintsPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(FontAccessPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(HardwareConcurrencyPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(HardwareDevicesPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ComputePressurePrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(AdditionalFingerprintPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(StandardFingerprintPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ResidualFingerprintPrivacy.ObservationScript);
        // Reproduce the production WebRTC bootstrap before the graphics check.
        if (!allowRtc) await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcPageGuard.Script);
        if (blockAudio) await core.AddScriptToExecuteOnDocumentCreatedAsync(AudioPageGuard.Script);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(AudioPageGuard.ObservationScript);
        await NavigateAsync(core, "about:blank");
        if (blockAudio && await core.ExecuteScriptAsync(AudioPageGuard.VerifyScript) != "true")
            throw new InvalidOperationException("Web Audio bootstrap failed.");
        if (!allowRtc && await core.ExecuteScriptAsync(WebRtcPageGuard.VerifyScript) != "true") throw new InvalidOperationException("WebRTC bootstrap failed.");
        if (allowRtc && await core.ExecuteScriptAsync("typeof RTCPeerConnection === 'function'") != "true")
            throw new InvalidOperationException("WebRTC Allow positive control failed.");
        if (normalizeDpr || expectedScale != 1)
        {
            var screen = await core.ExecuteScriptAsync(ScreenPrivacy.EvaluationScript);
            Console.WriteLine(label + " DPR bootstrap: " + screen + "; zoom=" + zoom);
            var screenResult = ScreenPrivacy.ReadResult(screen, zoom * expectedScale);
            if (screenResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(screenResult.Detail);
        }
        var speechBootstrap = await core.ExecuteScriptAsync(SpeechPrivacy.EvaluationScript);
        var speechOutcome = SpeechPrivacy.ReadResult(speechBootstrap).Outcome;
        Console.WriteLine(label + " speech bootstrap: " + speechBootstrap);
        if (speechOutcome != (blockSpeech ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
            throw new InvalidOperationException("Unexpected Speech Synthesis bootstrap.");
        var readback = await core.ExecuteScriptAsync(GraphicsRestriction.WebGlVerificationScript);
        var result = GraphicsRestriction.ReadWebGlResult(readback);
        Console.WriteLine(label + " blank: " + readback + "; " + result.Outcome);
        if (enforce && result.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(result.Detail);
        if (blockCanvas)
        {
            var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new {
                expression = CanvasReadback.EvaluationScript, awaitPromise = true, returnByValue = true }));
            var canvasResult = CanvasReadback.ReadCdpResult(response);
            Console.WriteLine(label + " canvas bootstrap: " + response + "; " + canvasResult.Outcome);
            if (canvasResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(canvasResult.Detail);
        }
        await core.AddScriptToExecuteOnDocumentCreatedAsync(CanvasReadback.Script);

        // Local HTTPS virtual host gives WebGPU a secure context, without contacting any external server.
        core.SetVirtualHostNameToFolderMapping("allmail-smoke.test", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        core.SetVirtualHostNameToFolderMapping("allmail-frame.test", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, e) => e.Cancel = !FingerprintProbePage.IsPageUri(e.Uri) && e.Uri != hintsServer?.Uri && e.Uri is not ("https://allmail-smoke.test/graphics.html" or "https://allmail-smoke.test/fingerprint.html" or "https://allmail-smoke.test/header-control.html");
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.WebMessageReceived += (_, e) => observed.TrySetResult(e.TryGetWebMessageAsString());
        core.Navigate("https://allmail-smoke.test/graphics.html");
        var json = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine(label + " HTTPS: " + json);
        using var document = JsonDocument.Parse(json);
        var observation = document.RootElement;
        if (observation.TryGetProperty("error", out _)) throw new InvalidOperationException("Secure graphics observation failed.");
        if (!observation.GetProperty("secureContext").GetBoolean() || !observation.GetProperty("canvas2D").GetBoolean())
            throw new InvalidOperationException("Secure context or CPU Canvas 2D unavailable.");
        foreach (var scope in new[] { "main", "worker" })
        {
            var canvas = observation.GetProperty(scope).GetProperty("canvasReadback");
            if (!canvas.GetProperty("offscreenDrawing").GetBoolean()) throw new InvalidOperationException("Offscreen 2D drawing failed.");
            var keys = scope == "main" ? new[] { "htmlGetImageData", "htmlToDataURL", "htmlToBlob", "offscreenGetImageData", "offscreenConvertToBlob" }
                : new[] { "offscreenGetImageData", "offscreenConvertToBlob" };
            foreach (var key in keys)
                if (canvas.GetProperty(key).GetString() != (blockCanvas ? "Blocked" : "Readable"))
                    throw new InvalidOperationException(label + ": wrong Canvas result: " + scope + " " + key);
        }
        var mainAudio = observation.GetProperty("main").GetProperty("webAudio");
        if (mainAudio.GetProperty("guardVerified").GetBoolean() != blockAudio
            || mainAudio.GetProperty("windowApisAvailable").GetBoolean() == blockAudio)
            throw new InvalidOperationException(label + ": unexpected Web Audio observation.");
        if (observation.GetProperty("worker").GetProperty("webAudio").GetProperty("windowApisAvailable").GetBoolean())
            throw new InvalidOperationException("Unexpected Window Web Audio API in worker.");
        if (!observation.GetProperty("htmlAudioApi").GetBoolean()) throw new InvalidOperationException("HTML Audio API removed.");
        foreach (var frame in observation.GetProperty("audioFrames").EnumerateArray())
            if (frame.GetProperty("guardVerified").GetBoolean() != blockAudio
                || frame.GetProperty("offlineRendered").GetBoolean() == blockAudio)
                throw new InvalidOperationException(label + ": frame Web Audio mismatch: " + frame);
        if (observation.GetProperty("audioFrames").GetArrayLength() != 3)
            throw new InvalidOperationException("Missing iframe observations.");
        if (observation.GetProperty("offlineRendered").GetBoolean() == blockAudio)
            throw new InvalidOperationException("Offline Web Audio render control failed.");
        // Require an immediate initial-empty-frame constructor control, independently of loaded frame coverage.
        var initialFrame = observation.GetProperty("initialFrame");
        if (initialFrame.GetProperty("constructorAvailable").GetBoolean() == blockAudio
            || initialFrame.GetProperty("constructorUsable").GetBoolean() == blockAudio)
            throw new InvalidOperationException("Initial empty iframe Web Audio control failed.");
        Console.WriteLine(label + " initial iframe: " + observation.GetProperty("initialFrame"));
        if (normalizeDpr || expectedScale != 1)
        {
            var scopes = observation.GetProperty("audioFrames").EnumerateArray().Select(f => f.GetProperty("screen"))
                .Append(observation.GetProperty("main").GetProperty("screen")).Append(initialFrame.GetProperty("screen"));
            foreach (var screen in scopes)
                if (ScreenPrivacy.ReadResult(screen.GetRawText(), zoom * expectedScale).Outcome != GraphicsReadbackOutcome.Verified)
                    throw new InvalidOperationException(label + ": DPR normalization failed: " + screen);
            if (observation.GetProperty("worker").GetProperty("screen").GetProperty("screenApisAvailable").GetBoolean())
                throw new InvalidOperationException("Unexpected worker Screen API.");
            if (await core.ExecuteScriptAsync("innerWidth > 0 && innerHeight > 0 && innerWidth < screen.width") != "true")
                throw new InvalidOperationException("Responsive viewport was replaced with a fixed screen-size viewport.");
            if (await core.ExecuteScriptAsync("Object.getOwnPropertyDescriptor(Screen.prototype,'width').get.toString().includes('[native code]')") != "true")
                throw new InvalidOperationException("Native Screen getter replaced.");
        }
        if (blockExtras)
        {
            foreach(var scope in observation.GetProperty("audioFrames").EnumerateArray().Append(observation.GetProperty("main")).Append(initialFrame).Append(observation.GetProperty("worker")))
            {
                if(ResidualFingerprintPrivacy.ReadResult(scope.GetProperty("residualPrivacy").GetRawText()).Outcome!=GraphicsReadbackOutcome.Verified)
                    throw new InvalidOperationException("Residual API document/frame/worker mismatch: "+scope);
                if(MathImplementationPrivacy.ReadResult(scope.GetProperty("residualPrivacy").GetProperty("mathPow").GetRawText())!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("HTTPS native pow mismatch: "+scope);
            }
            foreach(var defaults in observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("standardPrivacy"))
                .Append(observation.GetProperty("main").GetProperty("standardPrivacy")).Append(initialFrame.GetProperty("standardPrivacy")))
                if(StandardFingerprintPrivacy.ReadResult(defaults.GetRawText()).Outcome != GraphicsReadbackOutcome.Verified)
                    throw new InvalidOperationException(label + ": native document defaults scope mismatch: " + defaults);
            Console.WriteLine("PASS: native media/generic font defaults and local font source denial in main/child, loaded and initial frames.");
            var extras=observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("additionalPrivacy"))
                .Append(observation.GetProperty("main").GetProperty("additionalPrivacy")).Append(initialFrame.GetProperty("additionalPrivacy"));
            foreach(var extra in extras)
                if(AdditionalFingerprintPrivacy.ReadResult(extra.GetRawText()).Outcome!=GraphicsReadbackOutcome.Verified)
                    throw new InvalidOperationException(label + ": additional privacy scope mismatch: " + extra);
            var workerExtras=observation.GetProperty("worker").GetProperty("additionalPrivacy");
            if(AdditionalFingerprintPrivacy.ReadResult(workerExtras.GetRawText(),worker:true).Outcome!=GraphicsReadbackOutcome.Verified)
                throw new InvalidOperationException(label + ": worker network estimate mismatch: " + workerExtras);
        }
        if(label.StartsWith("legacy "))
        {
            foreach(var defaults in observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("standardPrivacy"))
                .Append(observation.GetProperty("main").GetProperty("standardPrivacy")).Append(initialFrame.GetProperty("standardPrivacy")))
                if (defaults.GetProperty("localFontLoad").ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Local font load positive control unavailable: " + defaults);
            var extra=observation.GetProperty("main").GetProperty("additionalPrivacy");
            foreach(var key in new[]{"xr","cpuPerformance","measureMemory","getDisplayMedia","selectAudioOutput","AmbientLightSensor","Magnetometer","NDEFReader","NDEFRecord","NDEFMessage"})
                if(!extra.GetProperty("apis").GetProperty(key).GetBoolean()) throw new InvalidOperationException("Native additional API positive control unavailable: " + key);
            if(extra.GetProperty("connection").GetProperty("effectiveType").GetString()!="slow-2g" || extra.GetProperty("connection").GetProperty("rtt").GetDouble()<1000 || extra.GetProperty("connection").GetProperty("downlink").GetDouble()>0.1)
                throw new InvalidOperationException("Slow-2G NQE positive control failed: " + extra);
            Console.WriteLine("PASS: native additional API positive control; forced WebXR/display capture/audio output/extra sensors; Slow-2G estimates.");
        }
        var speechScopes = observation.GetProperty("audioFrames").EnumerateArray().Select(f => f.GetProperty("speech"))
            .Append(observation.GetProperty("main").GetProperty("speech")).Append(initialFrame.GetProperty("speech"));
        foreach (var speech in speechScopes)
            if (SpeechPrivacy.ReadResult(speech.GetRawText()).Outcome != (blockSpeech ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                throw new InvalidOperationException("Unexpected Speech Synthesis scope: " + speech);
        if (SpeechPrivacy.ReadResult(observation.GetProperty("worker").GetProperty("speech").GetRawText()).Outcome != GraphicsReadbackOutcome.Verified)
            throw new InvalidOperationException("Unexpected Window Speech Synthesis API in worker.");
        if (observation.GetProperty("speechUsable").GetBoolean() == blockSpeech)
            throw new InvalidOperationException("Speech voice enumeration control failed.");
        foreach (var scope in observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("uaHints"))
            .Append(observation.GetProperty("main").GetProperty("uaHints")).Append(observation.GetProperty("worker").GetProperty("uaHints")))
            if (UserAgentHintsPrivacy.ReadResult(scope.GetRawText(), expectedUa).Outcome != (blockUaHints ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                throw new InvalidOperationException("Unexpected UA Client Hints scope: " + scope);
        foreach (var scope in observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("fontAccess"))
            .Append(observation.GetProperty("main").GetProperty("fontAccess")).Append(observation.GetProperty("initialFrame").GetProperty("fontAccess")))
            if (FontAccessPrivacy.ReadResult(scope.GetRawText()).Outcome != (blockFontAccess ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                throw new InvalidOperationException("Unexpected secure Local Font Access API availability: " + scope);
        foreach (var scope in observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("hardwareDevices"))
            .Append(observation.GetProperty("main").GetProperty("hardwareDevices")).Append(observation.GetProperty("initialFrame").GetProperty("hardwareDevices")))
            if (HardwareDevicesPrivacy.ReadResult(scope.GetRawText()).Outcome != (blockDevices ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                throw new InvalidOperationException("Hardware devices document scope mismatch: " + scope);
        foreach (var scope in observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("computePressure"))
            .Append(observation.GetProperty("main").GetProperty("computePressure")).Append(observation.GetProperty("initialFrame").GetProperty("computePressure")))
            if (ComputePressurePrivacy.ReadResult(scope.GetRawText()).Outcome != (blockPressure ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                throw new InvalidOperationException("Compute Pressure document scope mismatch: " + scope);
        var workerPressure = observation.GetProperty("worker").GetProperty("computePressure");
        if (ComputePressurePrivacy.ReadResult(workerPressure.GetRawText(),worker:true).Outcome != (blockPressure ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
            throw new InvalidOperationException("Compute Pressure dedicated worker scope mismatch: " + workerPressure);
        var workerDevices = observation.GetProperty("worker").GetProperty("hardwareDevices");
        if (HardwareDevicesPrivacy.ReadResult(workerDevices.GetRawText(),worker:true).Outcome != (blockDevices ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
            throw new InvalidOperationException("Hardware devices dedicated worker scope mismatch: " + workerDevices);
        var workerFont=observation.GetProperty("worker").GetProperty("fontAccess");
        if (workerFont.GetProperty("queryLocalFontsAvailable").GetBoolean() || workerFont.GetProperty("fontDataAvailable").GetBoolean())
            throw new InvalidOperationException("Unexpected Window font API in worker.");
        if (normalizeCpu)
        {
            if (UserAgentHintsBootstrap.ExpectedCpu(core)!=8) throw new InvalidOperationException("Production CPU bucket not derived from 13 control.");
            foreach (var scope in observation.GetProperty("audioFrames").EnumerateArray().Select(f=>f.GetProperty("cpu"))
                .Append(observation.GetProperty("main").GetProperty("cpu")).Append(observation.GetProperty("worker").GetProperty("cpu"))
                .Append(observation.GetProperty("initialFrame").GetProperty("cpu")))
                if (HardwareConcurrencyPrivacy.ReadResult(scope.GetRawText(),8).Outcome!=GraphicsReadbackOutcome.Verified)
                    throw new InvalidOperationException("Native CPU scope mismatch: " + scope);
        }
        if (hintsServer is not null) await CheckHttpHintsAsync(core, hintsServer, label, blockUaHints, expectedUa, UserAgentHintsBootstrap.ExpectedCpu(core), blockDevices, blockPressure, blockExtras);
        if (protocolFailure != "") throw new InvalidOperationException(protocolFailure);
        if (!enforce) return;
        foreach (var scope in new[] { "main", "worker" })
            foreach (var name in new[] { "webGl", "webGl2", "webGpuAdapter" })
                if (observation.GetProperty(scope).GetProperty(name).ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException(label + ": graphics remained available/unobserved: " + scope + " " + name);
        if (blockCanvas) await CheckBundledProbeAsync(core, environment, label, blockAudio, normalizeDpr, zoom, expectedScale, blockSpeech, blockUaHints, customUa, blockFontAccess, normalizeCpu, blockDevices, blockPressure, blockExtras);
    }

    private static async Task CheckBundledProbeAsync(CoreWebView2 core, CoreWebView2Environment environment, string label, bool blockAudio, bool normalizeDpr, double zoom, double expectedScale, bool blockSpeech, bool blockUaHints, string? customUa, bool blockFontAccess, bool normalizeCpu, bool blockDevices, bool blockPressure, bool blockExtras)
    {
        using var headerServer = new UaHintsServer();
        // Exercise the actual bundled report. All external HTTP is replaced locally; no route claims are tested.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.Host is "allmail-smoke.test" or FingerprintProbePage.Host or FingerprintProbePage.ContextHost
                || e.Request.Uri.StartsWith(headerServer.Uri,StringComparison.Ordinal)) return;
            if(uri.Host is "api.ipify.org" or "api6.ipify.org"
                && (!e.Request.Headers.Contains("Origin")||e.Request.Headers.GetHeader("Origin")!="https://diagnostics.invalid"))
                throw new InvalidOperationException("ipify requires a neutral diagnostic Origin for its CORS response.");
            e.Response = environment.CreateWebResourceResponse(new MemoryStream("{}"u8.ToArray()), 200, "OK",
                "Content-Type: application/json\r\nAccess-Control-Allow-Origin: *\r\n");
        };
        var policy = blockExtras ? GraphicsPolicy.StrictFingerprintExperimental : blockPressure ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental : blockDevices ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental : normalizeCpu ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental : blockFontAccess ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental : blockUaHints ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental : blockSpeech ? GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental : normalizeDpr ? GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental : blockAudio ? GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental : GraphicsPolicy.BlockWebGlWebGpuAndCanvasReadbackExperimental;
        await core.AddScriptToExecuteOnDocumentCreatedAsync("globalThis.__ppProbeSettings = " + JsonSerializer.Serialize(new {applicationVersion = FingerprintProbePage.ApplicationVersion, collectorHash = FingerprintProbePage.CollectorHash, expectedHardwareConcurrency = UserAgentHintsBootstrap.ExpectedCpu(core), graphicsPolicy = policy.ToString(), zoomFactor = zoom, browserTimeZoneId = "Europe/Riga", expectedUserAgent = blockUaHints ? core.Settings.UserAgent : null}) + ";");
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstUri = FingerprintProbePage.NavigationUri;
        core.WebMessageReceived += (_, e) =>
        {
            if (FingerprintProbePage.IsPageUri(e.Source)) observed.TrySetResult(e.TryGetWebMessageAsString());
        };
        var noStore = false;
        core.WebResourceResponseReceived += (_, e) => {
            if (FingerprintProbePage.IsPageUri(e.Request.Uri))
                noStore |= e.Response.Headers.Contains("Cache-Control") && e.Response.Headers.GetHeader("Cache-Control").Contains("no-store",StringComparison.OrdinalIgnoreCase);
        };
        await FingerprintProbePage.ConfigureAsync(core, environment);
        core.Navigate(firstUri);
        using var document = JsonDocument.Parse(await observed.Task.WaitAsync(TimeSpan.FromSeconds(25)));
        var report = document.RootElement;
        if (report.TryGetProperty("error", out _)) throw new InvalidOperationException("Bundled fingerprint report failed.");
        if (!noStore) throw new InvalidOperationException("Bundled collector response allowed persistent cache.");
        if (!FingerprintProbePage.IsCurrentReport(report.GetRawText())) throw new InvalidOperationException("Wrong bundled report version.");
        foreach (var oldVersion in new[] {7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23})
        {
            var stale=JsonSerializer.Serialize(new {reportVersion=oldVersion,applicationVersion=FingerprintProbePage.ApplicationVersion,collectorHash=FingerprintProbePage.CollectorHash});
            if (FingerprintProbePage.IsCurrentReport(stale)) throw new InvalidOperationException("Stale report accepted.");
        }
        if(!report.GetProperty("sections").TryGetProperty("Web Crypto — проверка шифрования",out _)||!report.GetProperty("sections").TryGetProperty("Исключения защиты профиля",out _))throw new InvalidOperationException("Compatibility sections omitted from exported report.");
        var verification = report.GetProperty("verification");
        foreach (var scope in new[] {"sameOriginFrame", "crossOriginFrame"})
        {
            if (verification.GetProperty(scope + "Consistency").GetString() != "Pass")
                throw new InvalidOperationException(label + " bundled iframe mismatch: " + scope + ": "
                    + report.GetProperty("contextObservations").GetProperty(scope).GetRawText() + "; "
                    + report.GetProperty("frameVerifications").GetProperty(scope).GetRawText());
        }
        foreach (var scope in new[] {"mainDocument", "dedicatedWorker", "sameOriginFrame", "crossOriginFrame"})
        {
            var context = report.GetProperty("contextObservations").GetProperty(scope);
            var intrinsics=context.GetProperty("javascriptIntrinsics");
            foreach(var key in new[]{"objectConstructor","arrayConstructor","errorConstructor","eventTargetConstructor","nativeConstructors"})if(intrinsics.GetProperty(key).ValueKind!=JsonValueKind.True)throw new InvalidOperationException("JavaScript intrinsic damaged: "+scope+" "+intrinsics);
            Console.WriteLine(label+" native JavaScript intrinsics "+scope+": "+intrinsics.GetRawText());
            var crypto=context.GetProperty("webCrypto");
            Console.WriteLine(label+" native Web Crypto "+scope+": "+crypto.GetRawText());
            foreach(var key in new[]{"secureContext","cryptoAvailable","subtleAvailable","nativeMethods","randomGeneration","sha256","aesGcmRoundTrip","aesGcmTamperRejected"})
                if(crypto.GetProperty(key).ValueKind!=JsonValueKind.True)throw new InvalidOperationException("Native Web Crypto failed: "+scope+" "+crypto);
            var remaining=context.GetProperty("residualPrivacy");
            if(blockExtras) {
                if(ResidualFingerprintPrivacy.ReadResult(remaining.GetRawText()).Outcome!=GraphicsReadbackOutcome.Verified||MathImplementationPrivacy.ReadResult(remaining.GetProperty("mathPow").GetRawText())!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Remaining bundled restrictions mismatch: "+scope+" "+remaining);
            }
            Console.WriteLine(label+" Remaining privacy "+scope+": "+remaining.GetRawText());
            var metrics=context.GetProperty("residualPrivacy").GetProperty("canvasTextMetrics");
            if(metrics.GetProperty("html").GetBoolean()!=(scope!="dedicatedWorker"&&!blockExtras)||metrics.GetProperty("offscreen").GetBoolean()==blockExtras)
                throw new InvalidOperationException("Bundled Canvas text metrics mismatch: "+scope+" "+metrics);
            Console.WriteLine(label+" Canvas text metrics "+scope+": "+metrics.GetRawText());
            if(scope is "sameOriginFrame" or "crossOriginFrame") {
                if(report.GetProperty("frameVerifications").GetProperty(scope).GetProperty("canvasTextMetrics").GetString()!=(blockExtras?"Pass":"NotApplicable"))throw new InvalidOperationException("Bundled frame metrics status mismatch.");
            }
            var webCodecs=context.GetProperty("media").GetProperty("webCodecs");
            if(webCodecs.EnumerateObject().Count()!=4 || webCodecs.EnumerateObject().Any(p=>p.Value.GetBoolean()==blockExtras))
                throw new InvalidOperationException("Bundled WebCodecs readback mismatch: "+scope+" "+webCodecs);
            if(scope is "sameOriginFrame" or "crossOriginFrame") {
                if(report.GetProperty("frameVerifications").GetProperty(scope).GetProperty("webCodecs").GetString()!=(blockExtras?"Pass":"NotApplicable"))
                    throw new InvalidOperationException("Bundled frame WebCodecs status mismatch: "+scope);
            }
            var timezone = context.GetProperty("timeZoneObservation");
            if (timezone.GetProperty("status").GetString() != "Observed" || timezone.GetProperty("timeZone").GetString() != "Europe/Riga"
                || timezone.GetProperty("offsets")[0].GetProperty("offset").GetInt32() != -120
                || timezone.GetProperty("offsets")[1].GetProperty("offset").GetInt32() != -180)
                throw new InvalidOperationException("Bundled seasonal timezone mismatch: " + scope + " " + timezone);
            if (context.GetProperty("media").GetProperty("status").GetString() != "Observed"
                || context.GetProperty("timer").GetProperty("status").GetString() != "Observed"
                || context.GetProperty("timer").GetProperty("samples").GetInt32() is < 1 or > 2048)
                throw new InvalidOperationException("Media/timer observations missing or unbounded: " + scope);
            if ((scope == "dedicatedWorker") != (context.GetProperty("media").GetProperty("htmlCanPlayType").ValueKind == JsonValueKind.Null))
                throw new InvalidOperationException("HTML codec observations confused with worker absence.");
            if (scope != "dedicatedWorker")
            {
                var formats = context.GetProperty("media").GetProperty("htmlCanPlayType");
                if (formats.EnumerateObject().Count() != 9 || formats.GetProperty("audio/mpeg").GetString() is not ("maybe" or "probably"))
                    throw new InvalidOperationException("Native HTML media support positive control missing: " + scope);
            }
            var timer = context.GetProperty("timer");
            var delta = timer.GetProperty("minPositiveDeltaMs");
            var positives = timer.GetProperty("positiveSamples").GetInt32();
            if (positives < 0 || positives > timer.GetProperty("samples").GetInt32()
                || (delta.ValueKind == JsonValueKind.Null) != (positives == 0)
                || delta.ValueKind != JsonValueKind.Null && (!double.IsFinite(delta.GetDouble()) || delta.GetDouble() <= 0)
                || timer.GetProperty("regressions").GetInt32() != 0)
                throw new InvalidOperationException("Invalid native timer observations: " + scope);
        }
        if (verification.GetProperty("allContextCoverage").GetString() != "NotPerformed"
            || report.GetProperty("contextCoverage").GetProperty("observed").GetArrayLength() != 4
            || !report.GetProperty("sections").TryGetProperty("Согласованность iframe",out _)
            || !report.GetProperty("sections").TryGetProperty("Медиакодеки и таймер",out _))
            throw new InvalidOperationException("Bundled iframe/media coverage missing or overstated.");
        Console.WriteLine(label + " PASS: bundled same-origin/cross-origin iframe consistency and bounded media/timer observations in four contexts.");
        Console.WriteLine(label + " remaining surfaces: " + report.GetProperty("sections").GetProperty("Медиакодеки и таймер").GetRawText());
        foreach(var scope in new[]{"MainDocument","DedicatedWorker"})
            if(verification.GetProperty("webCodecs"+scope).GetString()!=(blockExtras?"Pass":"NotApplicable"))
                throw new InvalidOperationException("Bundled WebCodecs status mismatch: "+scope);
        if(verification.GetProperty("workAreaMainDocument").GetString()!=(blockExtras?"Pass":"NotApplicable")||verification.GetProperty("workAreaDedicatedWorker").GetString()!="NotApplicable")throw new InvalidOperationException("Bundled work area status mismatch.");
        foreach(var scope in new[]{"MainDocument","DedicatedWorker"})
            foreach(var feature in new[]{"coarseClocks","mathPow","fontSetCheck"})
                if(verification.GetProperty(feature+scope).GetString()!=(blockExtras?"Pass":"NotApplicable"))throw new InvalidOperationException("Remaining bundled status mismatch: "+feature+scope);
        foreach(var scope in new[]{"MainDocument","DedicatedWorker"})
            if(verification.GetProperty("canvasTextMetrics"+scope).GetString()!=(blockExtras?"Pass":"NotApplicable"))throw new InvalidOperationException("Bundled metrics status mismatch.");
        if(verification.GetProperty("keyboardLayoutMainDocument").GetString()!=(blockExtras?"Pass":"NotApplicable")
            ||verification.GetProperty("keyboardLayoutDedicatedWorker").GetString()!="NotApplicable")throw new InvalidOperationException("Bundled keyboard layout status mismatch.");
        foreach(var scope in new[]{"sameOriginFrame","crossOriginFrame"})
            if(report.GetProperty("frameVerifications").GetProperty(scope).GetProperty("keyboardLayout").GetString()!=(blockExtras?"Pass":"NotApplicable"))
                throw new InvalidOperationException("Bundled frame keyboard layout mismatch: "+scope);
        if(verification.GetProperty("displayDiscoveryMainDocument").GetString()!=(blockExtras?"Pass":"NotApplicable")
            ||verification.GetProperty("displayDiscoveryDedicatedWorker").GetString()!="NotApplicable")throw new InvalidOperationException("Bundled display discovery status mismatch.");
        foreach(var scope in new[]{"sameOriginFrame","crossOriginFrame"})
            if(report.GetProperty("frameVerifications").GetProperty(scope).GetProperty("displayDiscovery").GetString()!=(blockExtras?"Pass":"NotApplicable"))
                throw new InvalidOperationException("Bundled frame display discovery mismatch: "+scope);
        foreach (var name in new[]{"computePressureMainDocument","computePressureDedicatedWorker"})
            if (verification.GetProperty(name).GetString() != (blockPressure ? "Pass" : "NotApplicable"))
                throw new InvalidOperationException("Bundled Compute Pressure status mismatch: " + name);
        foreach (var name in new[]{"hardwareDevicesMainDocument","hardwareDevicesDedicatedWorker"})
            if (verification.GetProperty(name).GetString() != (blockDevices ? "Pass" : "NotApplicable"))
                throw new InvalidOperationException("Bundled hardware devices status mismatch: " + name);
        foreach (var name in new[]{"hardwareConcurrencyMainDocument","hardwareConcurrencyDedicatedWorker"})
            if (verification.GetProperty(name).GetString()!=(normalizeCpu?"Pass":"NotApplicable"))
                throw new InvalidOperationException("Bundled CPU status mismatch: " + name);

        if (verification.GetProperty("localFontAccessMainDocument").GetString() != (blockFontAccess ? "Pass" : "NotApplicable")
            || verification.GetProperty("localFontAccessDedicatedWorker").GetString() != "NotApplicable")
            throw new InvalidOperationException("Bundled Local Font Access status incorrect.");
        if (report.GetProperty("sections").GetProperty("Шрифты").GetProperty("Список").GetString() is not {Length:>0})
            throw new InvalidOperationException("CSS font detection unexpectedly removed.");

        foreach (var name in new[] { "graphicsMainDocument", "graphicsDedicatedWorker", "canvasMainDocument", "canvasDedicatedWorker" })
            if (verification.GetProperty(name).GetString() != "Pass") throw new InvalidOperationException("Bundled probe failed: " + name);
        var graphics = report.GetProperty("sections").GetProperty("Графика и аппаратные отпечатки");
        if (graphics.GetProperty("Хэш Canvas").GetString() != "чтение заблокировано"
            || !graphics.TryGetProperty("Хэш Audio", out _) || !graphics.TryGetProperty("Math", out _))
            throw new InvalidOperationException("Canvas blocking aborted other fingerprint measurements.");
        if (verification.GetProperty("webAudioMainDocument").GetString() != (blockAudio ? "Pass" : "NotApplicable")
            || verification.GetProperty("webAudioDedicatedWorker").GetString() != "NotApplicable")
            throw new InvalidOperationException("Bundled Web Audio status incorrect.");
        if (verification.GetProperty("dprMainDocument").GetString() != (normalizeDpr ? "Pass" : "NotApplicable")
            || verification.GetProperty("dprDedicatedWorker").GetString() != "NotApplicable")
            throw new InvalidOperationException("Bundled screen status incorrect.");
        if (verification.GetProperty("speechSynthesisMainDocument").GetString() != (blockSpeech ? "Pass" : "NotApplicable")
            || verification.GetProperty("speechSynthesisDedicatedWorker").GetString() != "NotApplicable")
            throw new InvalidOperationException("Bundled Speech Synthesis status incorrect.");
        foreach (var name in new[] {"uaClientHintsMainDocument","uaClientHintsDedicatedWorker"})
            if (verification.GetProperty(name).GetString() != (blockUaHints ? "Pass" : "NotApplicable")) throw new InvalidOperationException("Bundled UA Client Hints status incorrect.");
        if (verification.GetProperty("uaClientHintsHttpEcho").GetString() != (blockUaHints ? "NotPerformed" : "NotApplicable"))
            throw new InvalidOperationException("Empty mocked echo must not prove HTTP hint suppression.");
        foreach(var name in new[]{"additionalApisMainDocument","hardwarePermissionsMainDocument","networkEstimatesMainDocument","networkEstimatesDedicatedWorker"})
            if(verification.GetProperty(name).GetString()!=(blockExtras?"Pass":"NotApplicable")) throw new InvalidOperationException("Bundled additional privacy status incorrect: " + name);
        if (verification.GetProperty("standardDefaultsMainDocument").GetString() != (blockExtras ? "Pass" : "NotApplicable")
            || verification.GetProperty("standardDefaultsDedicatedWorker").GetString() != "NotApplicable")
            throw new InvalidOperationException("Bundled native document defaults status mismatch.");
        foreach(var name in new[]{"residualApisMainDocument","residualApisDedicatedWorker"})
            if(verification.GetProperty(name).GetString()!=(blockExtras?"Pass":"NotApplicable"))throw new InvalidOperationException("Bundled residual privacy mismatch: "+name);
        if(!report.GetProperty("sections").TryGetProperty("Стандартные CSS-параметры и local(...)",out _)
            || !report.GetProperty("sections").TryGetProperty("Программные ограничения RAM и API",out _))throw new InvalidOperationException("New diagnostic sections were lost during ordering.");
        if(verification.GetProperty("additionalApisDedicatedWorker").GetString()!="NotApplicable") throw new InvalidOperationException("Window-only additional APIs incorrectly claim worker blocking.");
        if(!report.TryGetProperty("residualExposure",out var residual) || residual.GetProperty("deviceMemory").GetProperty("status").GetString()!=(blockExtras?"StandardizedByScript":"Visible") || residual.GetProperty("screen").GetProperty("status").GetString()!="DimensionsVisible" || residual.GetProperty("cssFonts").GetProperty("status").GetString()!="Visible")
            throw new InvalidOperationException("Residual fingerprint exposures hidden in report.");
        if(residual.GetProperty("math").GetProperty("status").GetString()!=(blockExtras?"NativePowStandardized":"Visible")||residual.GetProperty("timer").GetProperty("status").GetString()!=(blockExtras?"CoarsenedByScript":"Visible"))throw new InvalidOperationException("Partial Math/timing protection overstated in report.");
        if (blockSpeech && report.GetProperty("sections").GetProperty("Хранилище, устройства и разрешения").GetProperty("Голоса синтеза речи").GetString() != "Speech Synthesis недоступен")
            throw new InvalidOperationException("Bundled probe retained voice enumeration.");
        if (blockAudio && graphics.GetProperty("Хэш Audio").GetString() != "Web Audio заблокирован")
            throw new InvalidOperationException("Bundled probe retained Audio hash.");
        if (blockFontAccess)
        {
            foreach (var uri in new[] {firstUri,FingerprintProbePage.NavigationUri})
            {
                observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.Navigate(uri);
                var repeated=await observed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                if (!FingerprintProbePage.IsCurrentReport(repeated)) throw new InvalidOperationException("Rerun served incompatible collector.");
            }
            foreach (var stale in new[] {
                JsonSerializer.Serialize(new {reportVersion=FingerprintProbePage.ReportVersion,applicationVersion="0.1.10",collectorHash=FingerprintProbePage.CollectorHash}),
                JsonSerializer.Serialize(new {reportVersion=FingerprintProbePage.ReportVersion,applicationVersion=FingerprintProbePage.ApplicationVersion,collectorHash="stale"})})
                if (FingerprintProbePage.IsCurrentReport(stale)) throw new InvalidOperationException("Incompatible build/collector accepted.");
            Console.WriteLine(label + " PASS: embedded report v" + FingerprintProbePage.ReportVersion + " provenance; repeated same URL and fresh URL; stale report versions/build/hash rejected; collectorHash " + FingerprintProbePage.CollectorHash + ".");
        }
        Console.WriteLine(label + " bundled probe: report v" + FingerprintProbePage.ReportVersion + ", Canvas hash blocked; main/worker Canvas and graphics Pass; Web Audio " + (blockAudio ? "blocked, main Pass" : "unchanged") + "; DPR " + (normalizeDpr ? "main Pass" : "unchanged") + "; Speech Synthesis " + (blockSpeech ? "unavailable, main Pass, worker NotApplicable" : "unchanged") + "; UA Client Hints " + (blockUaHints ? "main/worker Pass; empty HTTP echo NotPerformed" : "unchanged") + "; Local Font Access " + (blockFontAccess ? "main Pass, worker NotApplicable; CSS fonts retained" : "unchanged") + "; CPU " + (normalizeCpu ? "main/worker Pass, native count 8" : "unchanged") + "; Hardware devices " + (blockDevices ? "main/worker Pass" : "unchanged") + "; Compute Pressure " + (blockPressure ? "main/worker Pass" : "unchanged") + "; Additional privacy " + (blockExtras ? "APIs/permissions main Pass, worker APIs NotApplicable; network main/worker Pass" : "unchanged") + "; build " + FingerprintProbePage.ApplicationVersion + "; Math retained; HTTP mocked locally.");
        await CheckDiagnosticHeadersAsync(core,headerServer,label);
    }

    private static async Task CheckDiagnosticHeadersAsync(CoreWebView2 core,UaHintsServer server,string label)
    {
        async Task<Dictionary<string,string>> ReceivedHeadersAsync(bool forceReferer=false)
        {
            using var response=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {
                expression=$"fetch({JsonSerializer.Serialize(server.Uri+"echo")},{{credentials:'omit',referrerPolicy:{JsonSerializer.Serialize(forceReferer?"unsafe-url":"no-referrer")}}}).then(r=>{{if(!r.ok)throw new Error(r.status);return r.json();}})",
                awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(10)));
            if(response.RootElement.TryGetProperty("exceptionDetails",out var error))throw new InvalidOperationException("Diagnostic header receiver failed: "+error);
            return JsonSerializer.Deserialize<Dictionary<string,string>>(response.RootElement.GetProperty("result").GetProperty("value").GetRawText())!;
        }
        var diagnostic=await ReceivedHeadersAsync();
        if(!diagnostic.Keys.Any(k=>k.Equals("User-Agent",StringComparison.OrdinalIgnoreCase))
            || diagnostic.Keys.Any(k=>k.Equals("Origin",StringComparison.OrdinalIgnoreCase)||k.Equals("Referer",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Embedded collector exposed Origin/Referer to real HTTP receiver.");

        const string controlUri="https://allmail-smoke.test/header-control.html";
        // Virtual-host folder requests bypass WebResourceRequested. Serve a real
        // copied fixture file, as for graphics.html, rather than an event response.
        await NavigateAsync(core,controlUri);
        var control=await ReceivedHeadersAsync(forceReferer:true);
        if(!control.Any(p=>p.Key.Equals("Origin",StringComparison.OrdinalIgnoreCase)&&p.Value=="https://allmail-smoke.test")
            || !control.Any(p=>p.Key.Equals("Referer",StringComparison.OrdinalIgnoreCase)&&p.Value==controlUri))
            throw new InvalidOperationException("Ordinary site Origin/Referer changed or positive control unavailable.");
        Console.WriteLine(label+" PASS: diagnostic Origin/Referer absent at real HTTP receiver; ordinary site Origin/Referer retained; browser CORS response readable.");
    }

    private static async Task CheckHttpHintsAsync(CoreWebView2 core, UaHintsServer server, string label, bool restricted, string expectedUa, int? expectedCpu, bool blockDevices, bool blockPressure, bool blockExtras)
    {
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (e.Source != server.Uri) return;
            var message = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("progress", out var progress)) Console.WriteLine(label + " HTTP fixture: " + progress.GetString());
            else observed.TrySetResult(message);
        }
        core.WebMessageReceived += Received;
        try
        {
            core.Navigate(server.Uri);
            var json = await observed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine(label + " loopback HTTP hints: " + json);
            using var document = JsonDocument.Parse(json);
            if(label.StartsWith("legacy ") && !document.RootElement.GetProperty("service").GetProperty("additionalPrivacy").GetProperty("apis").GetProperty("measureMemory").GetBoolean())
                throw new InvalidOperationException("ServiceWorker memory API positive control unavailable.");
            if(blockExtras)
                foreach(var scope in (blockExtras?new[]{"main","dedicated"}:new[]{"main","dedicated","service"}))
                {
                    if(ResidualFingerprintPrivacy.ReadResult(document.RootElement.GetProperty(scope).GetProperty("residualPrivacy").GetRawText()).Outcome!=GraphicsReadbackOutcome.Verified)
                        throw new InvalidOperationException("Early residual API startup mismatch: "+scope);
                    var extra=document.RootElement.GetProperty(scope).GetProperty("additionalPrivacy");
                    if(AdditionalFingerprintPrivacy.ReadResult(extra.GetRawText(),worker:scope!="main").Outcome!=GraphicsReadbackOutcome.Verified)
                        throw new InvalidOperationException("Early additional privacy scope mismatch: " + scope + " " + extra);
                }
            foreach (var scope in new[]{"main","dedicated"})
                if (ComputePressurePrivacy.ReadResult(document.RootElement.GetProperty(scope).GetProperty("computePressure").GetRawText(),worker:scope != "main").Outcome != (blockPressure ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                    throw new InvalidOperationException("Early Compute Pressure scope mismatch: " + scope);
            if(blockExtras) {
                var service=document.RootElement.GetProperty("service");
                if(service.GetProperty("status").GetString()!="NotApplicable" || service.GetProperty("containerAvailable").GetBoolean())throw new InvalidOperationException("Strict service worker API remains available.");
            } else {
                var servicePressure=document.RootElement.GetProperty("service").GetProperty("computePressure");
                if(servicePressure.GetProperty("observerAvailable").GetBoolean() || servicePressure.GetProperty("recordAvailable").GetBoolean())throw new InvalidOperationException("Unexpected Compute Pressure API in ServiceWorker.");
            }
            if (blockDevices)
                foreach (var scope in (blockExtras?new[]{"main","dedicated"}:new[]{"main","dedicated","service"}))
                    if (HardwareDevicesPrivacy.ReadResult(document.RootElement.GetProperty(scope).GetProperty("hardwareDevices").GetRawText(),worker:scope != "main").Outcome != GraphicsReadbackOutcome.Verified)
                        throw new InvalidOperationException("Early hardware devices scope mismatch: " + scope);
            if (expectedCpu is not null)
                foreach (var scope in (blockExtras?new[]{"main","dedicated"}:new[]{"main","dedicated","service"}))
                {
                    var cpu=document.RootElement.GetProperty(scope).GetProperty("cpu");
                    if (HardwareConcurrencyPrivacy.ReadResult(cpu.GetRawText(),expectedCpu).Outcome!=GraphicsReadbackOutcome.Verified)
                        throw new InvalidOperationException("Early CPU in HTTP worker scope mismatch: " + scope + " " + cpu);
                }
            foreach (var scope in new[] {"main","dedicated","shared","service"})
            {
                var value = document.RootElement.GetProperty(scope);
                if(blockExtras && scope=="service")continue;
                if (restricted && scope == "shared")
                {
                    if (value.GetProperty("status").GetString() != "NotApplicable" || value.GetProperty("constructorAvailable").GetBoolean())
                        throw new InvalidOperationException("SharedWorker must be natively unavailable in the UA hints mode.");
                    continue;
                }
                if (UserAgentHintsPrivacy.ReadResult(value.GetProperty("observation").GetRawText(), expectedUa).Outcome != (restricted ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                    throw new InvalidOperationException("Loopback native UA hints mismatch: " + scope);
                var headers = value.GetProperty("headers").EnumerateObject().ToDictionary(p=>p.Name, p=>p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
                if (!headers.TryGetValue("Authorization",out var authorization) || authorization != "ProtonProfiles-Fixture"
                    || !headers.TryGetValue("Cookie",out var cookie) || !cookie.Contains("pp-fixture=1",StringComparison.Ordinal))
                    throw new InvalidOperationException("HTTP authentication/cookie fixture headers were not preserved: " + scope);
                var memoryHints = headers.Where(p=>p.Key.Equals("Sec-CH-Device-Memory",StringComparison.OrdinalIgnoreCase) || p.Key.Equals("Device-Memory",StringComparison.OrdinalIgnoreCase)).ToArray();
                if (blockExtras && headers.Keys.Any(StandardFingerprintPrivacy.IsClientHintHeader))
                    throw new InvalidOperationException("Strict client hint header suppression failed at receiver: " + scope);
                if (label.StartsWith("legacy ") && scope == "main" && memoryHints.Length != 2)
                    throw new InvalidOperationException("Memory Client Hints receiver positive control unavailable.");
                var memory = value.GetProperty("deviceMemory");
                foreach (var hint in memoryHints)
                    if (!double.TryParse(hint.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var bucket)
                        || !memory.TryGetDouble(out var observedBucket) || bucket != observedBucket)
                        throw new InvalidOperationException("Memory HTTP/JavaScript bucket mismatch: " + scope);
                Console.WriteLine(label + " " + scope + " memory Client Hints requested: " + JsonSerializer.Serialize(new {deviceMemory=memory,hintHeaders=memoryHints}));
                if (!headers.TryGetValue("User-Agent", out var ua) || ua != expectedUa) throw new InvalidOperationException("HTTP UA differs from native setting.");
                if (restricted ? headers.Keys.Any(k=>k.Equals("Sec-CH-UA",StringComparison.OrdinalIgnoreCase) || k.StartsWith("Sec-CH-UA-",StringComparison.OrdinalIgnoreCase))
                    : scope == "main" && (!headers.TryGetValue("Sec-CH-UA-Full-Version-List", out var full) || string.IsNullOrWhiteSpace(full)))
                    throw new InvalidOperationException("UA hints HTTP control failed: " + scope);
                if (!restricted && scope != "main") Console.WriteLine(label + " " + scope + " baseline worker HTTP UA hints: "
                    + (headers.Keys.Any(k=>k.StartsWith("Sec-CH-UA",StringComparison.OrdinalIgnoreCase)) ? "Observed" : "NotApplicable (naturally absent)") + "; JS UAData control remains required.");
            }
        }
        finally { core.WebMessageReceived -= Received; }
    }

    private static async Task NavigateAsync(CoreWebView2 core, string uri)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) => ready.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(uri);
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Navigation failed.");
        }
        finally { core.NavigationCompleted -= Completed; }
    }
}
