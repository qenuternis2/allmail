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
    private static int Main()
    {
        var exitCode = 1;
        var root = Path.Combine(Path.GetTempPath(), "allmail-graphics-smoke-" + Guid.NewGuid().ToString("N"));
        var app = new Application();
        var window = new Window { Width = 400, Height = 300, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Loaded += async (_, _) =>
        {
            try
            {
                Console.WriteLine("WebView2 Runtime: " + CoreWebView2Environment.GetAvailableBrowserVersionString());
                // Observe the old behavior, without requiring GPU availability on the CI machine.
                await RunAsync(window, root, "legacy", "--disable-webgl --disable-features=WebGPU,WebGPUService", enforce: false)
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
                await RunAsync(window, root, "ua-hints-custom", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental), enforce: false, blockCanvas: true, blockAudio: true, allowRtc: true, normalizeDpr: true, blockSpeech: true, blockUaHints: true, customUa: "allmail-smoke/1.0")
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native UA Client Hints restriction; reduced Chromium/custom UA consistent; production secure bootstrap; main/child/loaded frames/dedicated/shared/service workers; actual loopback HTTP receiver with Accept-CH; previous privacy checks retained.");
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

    private static async Task RunAsync(Window window, string root, string label, string arguments, bool enforce, bool blockCanvas = false, bool blockAudio = false, bool allowRtc = false, bool normalizeDpr = false, double expectedScale = 1, bool blockSpeech = false, bool blockUaHints = false, string? customUa = null)
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
            await CheckViewAsync(main, environment, label + " main", enforce, blockCanvas, blockAudio, allowRtc, normalizeDpr, 1, expectedScale, blockSpeech, blockUaHints, customUa);
            if (enforce)
            {
                using var child = new WebView2();
                grid.Children.Add(child);
                await CheckViewAsync(child, environment, label + " child", enforce, blockCanvas, blockAudio, allowRtc, normalizeDpr, normalizeDpr ? 1.25 : 1, expectedScale, blockSpeech, blockUaHints, customUa);
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

    private static async Task CheckViewAsync(WebView2 view, CoreWebView2Environment environment, string label, bool enforce, bool blockCanvas, bool blockAudio, bool allowRtc, bool normalizeDpr, double zoom, double expectedScale, bool blockSpeech, bool blockUaHints, string? customUa)
    {
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        var config = new ProfileConfig {Id = Guid.NewGuid(), DisplayName = label, GraphicsPolicy = blockUaHints ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental : GraphicsPolicy.RuntimeDefault,
            UserAgentMode = customUa is null ? UserAgentMode.Default : UserAgentMode.Custom, CustomUserAgent = customUa};
        var expectedUa = blockUaHints ? UserAgentHintsPrivacy.UserAgentToApply(config,core.Settings.UserAgent) : customUa ?? core.Settings.UserAgent;
        var protocolFailure = "";
        await UserAgentHintsBootstrap.ApplyAsync(core, config, onFailure: reason => { protocolFailure = reason; Console.Error.WriteLine(reason); return Task.CompletedTask; });
        await UserAgentHintsBootstrap.VerifyAsync(core, environment, config, verify: true, diagnostic:json=>Console.WriteLine(label + " secure UA hints bootstrap: " + json));
        using var hintsServer = blockUaHints || label.StartsWith("legacy ", StringComparison.Ordinal) ? new UaHintsServer() : null;

        view.ZoomFactor = zoom;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ScreenPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(SpeechPrivacy.ObservationScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(UserAgentHintsPrivacy.ObservationScript);
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
        core.NavigationStarting += (_, e) => e.Cancel = e.Uri != hintsServer?.Uri && e.Uri is not ("https://allmail-smoke.test/graphics.html" or "https://allmail-smoke.test/fingerprint.html");
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
        if (hintsServer is not null) await CheckHttpHintsAsync(core, hintsServer, label, blockUaHints, expectedUa);
        if (protocolFailure != "") throw new InvalidOperationException(protocolFailure);
        if (!enforce) return;
        foreach (var scope in new[] { "main", "worker" })
            foreach (var name in new[] { "webGl", "webGl2", "webGpuAdapter" })
                if (observation.GetProperty(scope).GetProperty(name).ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException(label + ": graphics remained available/unobserved: " + scope + " " + name);
        if (blockCanvas) await CheckBundledProbeAsync(core, environment, label, blockAudio, normalizeDpr, zoom, expectedScale, blockSpeech, blockUaHints, customUa);
    }

    private static async Task CheckBundledProbeAsync(CoreWebView2 core, CoreWebView2Environment environment, string label, bool blockAudio, bool normalizeDpr, double zoom, double expectedScale, bool blockSpeech, bool blockUaHints, string? customUa)
    {
        // Exercise the actual bundled report. All external HTTP is replaced locally; no route claims are tested.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.Host == "allmail-smoke.test") return;
            e.Response = environment.CreateWebResourceResponse(new MemoryStream("{}"u8.ToArray()), 200, "OK",
                "Content-Type: application/json\r\nAccess-Control-Allow-Origin: *\r\n");
        };
        var policy = blockUaHints ? GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental : blockSpeech ? GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental : normalizeDpr ? GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental : blockAudio ? GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental : GraphicsPolicy.BlockWebGlWebGpuAndCanvasReadbackExperimental;
        await core.AddScriptToExecuteOnDocumentCreatedAsync("globalThis.__ppProbeSettings = " + JsonSerializer.Serialize(new {graphicsPolicy = policy.ToString(), zoomFactor = zoom, expectedUserAgent = blockUaHints ? core.Settings.UserAgent : null}) + ";");
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.WebMessageReceived += (_, e) =>
        {
            if (e.Source == "https://allmail-smoke.test/fingerprint.html") observed.TrySetResult(e.TryGetWebMessageAsString());
        };
        core.Navigate("https://allmail-smoke.test/fingerprint.html");
        using var document = JsonDocument.Parse(await observed.Task.WaitAsync(TimeSpan.FromSeconds(25)));
        var report = document.RootElement;
        if (report.TryGetProperty("error", out _)) throw new InvalidOperationException("Bundled fingerprint report failed.");
        if (report.GetProperty("reportVersion").GetInt32() != 8) throw new InvalidOperationException("Wrong bundled report version.");
        var verification = report.GetProperty("verification");
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
        if (blockSpeech && report.GetProperty("sections").GetProperty("Хранилище, устройства и разрешения").GetProperty("Голоса синтеза речи").GetString() != "Speech Synthesis недоступен")
            throw new InvalidOperationException("Bundled probe retained voice enumeration.");
        if (blockAudio && graphics.GetProperty("Хэш Audio").GetString() != "Web Audio заблокирован")
            throw new InvalidOperationException("Bundled probe retained Audio hash.");
        Console.WriteLine(label + " bundled probe: report v8, Canvas hash blocked; main/worker Canvas and graphics Pass; Web Audio " + (blockAudio ? "blocked, main Pass" : "unchanged") + "; DPR " + (normalizeDpr ? "main Pass" : "unchanged") + "; Speech Synthesis " + (blockSpeech ? "unavailable, main Pass, worker NotApplicable" : "unchanged") + "; UA Client Hints " + (blockUaHints ? "main/worker Pass; empty HTTP echo NotPerformed" : "unchanged") + "; Math retained; HTTP mocked locally.");
    }

    private static async Task CheckHttpHintsAsync(CoreWebView2 core, UaHintsServer server, string label, bool restricted, string expectedUa)
    {
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs e) { if (e.Source == server.Uri) observed.TrySetResult(e.TryGetWebMessageAsString()); }
        core.WebMessageReceived += Received;
        try
        {
            core.Navigate(server.Uri);
            var json = await observed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine(label + " loopback HTTP hints: " + json);
            using var document = JsonDocument.Parse(json);
            foreach (var scope in new[] {"main","dedicated","shared","service"})
            {
                var value = document.RootElement.GetProperty(scope);
                if (UserAgentHintsPrivacy.ReadResult(value.GetProperty("observation").GetRawText(), expectedUa).Outcome != (restricted ? GraphicsReadbackOutcome.Verified : GraphicsReadbackOutcome.Violation))
                    throw new InvalidOperationException("Loopback native UA hints mismatch: " + scope);
                var headers = value.GetProperty("headers").EnumerateObject().ToDictionary(p=>p.Name, p=>p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
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
