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
            var pending = new List<Task>();var cancelled = 0;
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) cancelled++; };
            core.AddWebResourceRequestedFilter("https://bootstrap-race.invalid/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var deferral = e.GetDeferral();started.TrySetResult();
                async Task Reply()
                {
                    try
                    {
                        await Task.Delay(100);
                        e.Response = environment.CreateWebResourceResponse(new MemoryStream("<!doctype html><title>Cancelled old request</title>"u8.ToArray()), 200, "OK", "Content-Type: text/html");
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
                await UserAgentHintsBootstrap.VerifyAsync(core, environment, config, verify: true);
                if (core.Source != "https://ua-hints-bootstrap.protonprofiles.invalid/") throw new InvalidOperationException("Bootstrap evaluated a different document.");
            }
            await Task.WhenAll(pending);
            if (cancelled < 3) throw new InvalidOperationException("Cancelled-navigation negative control was not observed.");
            Console.WriteLine("PASS: production UA bootstrap ignores three real cancelled prior navigations, waits for its own NavigationId and verifies the protected secure document.");
        }
        finally { window.Content = null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
}
