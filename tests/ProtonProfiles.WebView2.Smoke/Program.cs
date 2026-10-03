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

    private static async Task RunAsync(Window window, string root, string label, string arguments, bool enforce)
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
            await CheckViewAsync(main, environment, label + " main", enforce);
            if (enforce)
            {
                using var child = new WebView2();
                grid.Children.Add(child);
                await CheckViewAsync(child, environment, label + " child", enforce);
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

    private static async Task CheckViewAsync(WebView2 view, CoreWebView2Environment environment, string label, bool enforce)
    {
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        // Reproduce the production WebRTC bootstrap before the graphics check.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcPageGuard.Script);
        await NavigateAsync(core, "about:blank");
        if (await core.ExecuteScriptAsync(WebRtcPageGuard.VerifyScript) != "true") throw new InvalidOperationException("WebRTC bootstrap failed.");
        var readback = await core.ExecuteScriptAsync(GraphicsRestriction.WebGlVerificationScript);
        var result = GraphicsRestriction.ReadWebGlResult(readback);
        Console.WriteLine(label + " blank: " + readback + "; " + result.Outcome);
        if (enforce && result.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(result.Detail);

        // Local HTTPS virtual host gives WebGPU a secure context, without contacting any external server.
        core.SetVirtualHostNameToFolderMapping("allmail-smoke.test", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, e) => e.Cancel = e.Uri != "https://allmail-smoke.test/graphics.html";
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
        if (!enforce) return;
        foreach (var scope in new[] { "main", "worker" })
            foreach (var name in new[] { "webGl", "webGl2", "webGpuAdapter" })
                if (observation.GetProperty(scope).GetProperty(name).ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException(label + ": graphics remained available/unobserved: " + scope + " " + name);
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
