using System.IO;
using System.Text.Json;
using System.Windows;
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
                await RunAsync(window, root, "restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockWebGlAndWebGpuExperimental), enforce: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native WebView2 graphics restriction; main, child and dedicated worker; CPU Canvas 2D works.");
                await RunAsync(window, root, "canvas-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockWebGlWebGpuAndCanvasReadbackExperimental), enforce: true, blockCanvas: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: native Canvas readback restriction; main/child/dedicated worker; drawing commands accepted, pixel/blob exports blocked.");
                await RunAsync(window, root, "audio-restricted", BrowserArguments.Build(null, graphics: GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental), enforce: true, blockCanvas: true, blockAudio: true, allowRtc: true)
                    .WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS: document Web Audio restriction; main/child/loaded same-origin, srcdoc and cross-origin frames; worker APIs naturally absent; HTML media APIs retained; WebRTC Allow control passed.");
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

    private static async Task RunAsync(Window window, string root, string label, string arguments, bool enforce, bool blockCanvas = false, bool blockAudio = false, bool allowRtc = false)
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
            await CheckViewAsync(main, environment, label + " main", enforce, blockCanvas, blockAudio, allowRtc);
            if (enforce)
            {
                using var child = new WebView2();
                grid.Children.Add(child);
                await CheckViewAsync(child, environment, label + " child", enforce, blockCanvas, blockAudio, allowRtc);
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

    private static async Task CheckViewAsync(WebView2 view, CoreWebView2Environment environment, string label, bool enforce, bool blockCanvas, bool blockAudio, bool allowRtc)
    {
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
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
        core.NavigationStarting += (_, e) => e.Cancel = e.Uri is not ("https://allmail-smoke.test/graphics.html" or "https://allmail-smoke.test/fingerprint.html");
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
        // Initial empty iframes are recorded separately: injection there is not assumed from loaded frame coverage.
        Console.WriteLine(label + " initial iframe: " + observation.GetProperty("initialFrame"));
        if (!enforce) return;
        foreach (var scope in new[] { "main", "worker" })
            foreach (var name in new[] { "webGl", "webGl2", "webGpuAdapter" })
                if (observation.GetProperty(scope).GetProperty(name).ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException(label + ": graphics remained available/unobserved: " + scope + " " + name);
        if (blockCanvas) await CheckBundledProbeAsync(core, environment, label, blockAudio);
    }

    private static async Task CheckBundledProbeAsync(CoreWebView2 core, CoreWebView2Environment environment, string label, bool blockAudio)
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
        var policy = blockAudio ? GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental : GraphicsPolicy.BlockWebGlWebGpuAndCanvasReadbackExperimental;
        await core.AddScriptToExecuteOnDocumentCreatedAsync("globalThis.__ppProbeSettings = {graphicsPolicy:'" + policy + "'};");
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.WebMessageReceived += (_, e) =>
        {
            if (e.Source == "https://allmail-smoke.test/fingerprint.html") observed.TrySetResult(e.TryGetWebMessageAsString());
        };
        core.Navigate("https://allmail-smoke.test/fingerprint.html");
        using var document = JsonDocument.Parse(await observed.Task.WaitAsync(TimeSpan.FromSeconds(25)));
        var report = document.RootElement;
        if (report.TryGetProperty("error", out _)) throw new InvalidOperationException("Bundled fingerprint report failed.");
        if (report.GetProperty("reportVersion").GetInt32() != 5) throw new InvalidOperationException("Wrong bundled report version.");
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
        if (blockAudio && graphics.GetProperty("Хэш Audio").GetString() != "Web Audio заблокирован")
            throw new InvalidOperationException("Bundled probe retained Audio hash.");
        Console.WriteLine(label + " bundled probe: report v5, Canvas hash blocked; main/worker Canvas and graphics Pass; Web Audio " + (blockAudio ? "blocked, main Pass" : "unchanged") + "; Math retained; HTTP mocked locally.");
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
