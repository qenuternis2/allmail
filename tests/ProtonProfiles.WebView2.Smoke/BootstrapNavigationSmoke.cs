using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;

internal static class BootstrapNavigationSmoke
{
    public static async Task RunAsync(Window window, string root)
    {
        var config = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Bootstrap race fixture",
            GraphicsPolicy = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental };
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "bootstrap-race"),
            new() { AdditionalBrowserArguments = BrowserArguments.Build(null, graphics: config.GraphicsPolicy), ExclusiveUserDataFolderAccess = true });
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        try
        {
            using var view = new WebView2();window.Content = view;
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            await UserAgentHintsBootstrap.ApplyAsync(core, config);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new List<Task>();var previousIds = new HashSet<ulong>();var previousCompleted = 0;var overlapping = 0;var verifying = false;
            core.NavigationStarting += (_, e) => { if (e.Uri.StartsWith("https://bootstrap-race.invalid/",StringComparison.Ordinal)) previousIds.Add(e.NavigationId); };
            core.NavigationCompleted += (_, e) =>
            {
                if (!previousIds.Contains(e.NavigationId)) return;
                previousCompleted++;if(verifying)overlapping++;
                Console.WriteLine($"Bootstrap previous navigation completed: id={e.NavigationId}; success={e.IsSuccess}; error={e.WebErrorStatus}; overlaps={verifying}");
            };
            core.AddWebResourceRequestedFilter("https://bootstrap-race.invalid/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    // Commit a real document, but hold load completion with its image request.
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream("<!doctype html><img src='hold.png'>"u8.ToArray()),200,"OK","Content-Type: text/html\r\nCache-Control: no-store");
                    return;
                }
                if (e.ResourceContext != CoreWebView2WebResourceContext.Image)
                {
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream([]),404,"Not Found","Cache-Control: no-store");
                    return;
                }
                var deferral = e.GetDeferral();started.TrySetResult();
                async Task Reply()
                {
                    try
                    {
                        await Task.Delay(100);
                        e.Response = environment.CreateWebResourceResponse(new MemoryStream([]), 200, "OK", "Content-Type: image/png\r\nCache-Control: no-store");
                    }
                    catch (COMException) { } // The deliberately superseded request can already be gone.
                    finally { deferral.Complete(); }
                }
                pending.Add(Reply());
            };
            for (var attempt = 0; attempt < 3; attempt++)
            {
                started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                core.Navigate("https://bootstrap-race.invalid/" + attempt);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                verifying = true;
                await UserAgentHintsBootstrap.VerifyAsync(core, environment, config, verify: true);
                verifying = false;
                if (core.Source != "https://ua-hints-bootstrap.protonprofiles.invalid/") throw new InvalidOperationException("Bootstrap evaluated a different document.");
            }
            await Task.WhenAll(pending);
            if (previousCompleted < 3 || overlapping < 1) throw new InvalidOperationException($"Prior-navigation completion negative control missing: completed={previousCompleted}; overlapping={overlapping}");
            Console.WriteLine("PASS: production UA bootstrap ignores real overlapping prior-navigation completions, waits for its own NavigationId and verifies three protected secure documents.");
        }
        finally { window.Content = null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
}
