using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;

internal static class FingerprintInterfaceSmoke
{
    public static async Task RunAsync(Window window, string root)
    {
        using var resource = typeof(FingerprintProbePage).Assembly.GetManifestResourceStream("ProtonProfiles.App.Diagnostics.fingerprint.html")!;
        using var reader = new StreamReader(resource);
        var source = await reader.ReadToEndAsync();
        // Exercise the production renderer/styles with synthetic data; never
        // start the collectors or contact the diagnostic's external services.
        var html = Regex.Replace(source, "<script\\b[^>]*>.*?</script>", "", RegexOptions.Singleline);
        var start = source.IndexOf("function esc(s)", StringComparison.Ordinal);
        var end = source.IndexOf("async function run()", StringComparison.Ordinal);
        var render = "const $ = id => document.getElementById(id);const report = {checks:[],sections:{'Test section':{'Ядра CPU (hardwareConcurrency)':8,'User-Agent':'A'.repeat(200)}}};"
            + source[start..end] + "render('synthetic');";
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "fingerprint-interface"),
            new() { ExclusiveUserDataFolderAccess = true });
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        try
        {
            window.Width = 960; window.Height = 760;
            using var view = new WebView2 { HorizontalAlignment = HorizontalAlignment.Left, Width = 900 };
            window.Content = view;
            await view.EnsureCoreWebView2Async(environment);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.CoreWebView2.NavigationCompleted += (_, e) => ready.TrySetResult(e.IsSuccess);
            view.CoreWebView2.NavigateToString(html);
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("UI fixture navigation failed.");
            await view.CoreWebView2.ExecuteScriptAsync(render);
            foreach (var width in new[] { 320, 480, 900 })
            {
                view.Width = width; window.UpdateLayout(); await Task.Delay(150);
                using var observed = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("""
                    (() => ({width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth,
                      headers:document.querySelectorAll('th[scope="row"]').length,
                      tables:document.querySelectorAll('table[aria-label]').length,
                      live:document.getElementById('status').getAttribute('aria-live'),main:!!document.querySelector('main'),
                      valueWidth:document.querySelector('td.v').getBoundingClientRect().width}))()
                    """));
                var o = observed.RootElement;
                if (o.GetProperty("overflow").GetBoolean() || o.GetProperty("headers").GetInt32() != 2
                    || o.GetProperty("tables").GetInt32() != 1 || o.GetProperty("live").GetString() != "polite"
                    || !o.GetProperty("main").GetBoolean() || width <= 480 && o.GetProperty("valueWidth").GetDouble() < 200)
                    throw new InvalidOperationException("Fingerprint interface accessibility/reflow regression: " + observed.RootElement);
                Console.WriteLine("PASS: native fingerprint interface width " + width + "; " + observed.RootElement);
            }
            view.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            await Task.Delay(150);
            using var dark = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({background:getComputedStyle(document.body).backgroundColor,scheme:getComputedStyle(document.documentElement).colorScheme})"));
            var darkText = dark.RootElement.GetString()!;
            if (!darkText.Contains("30, 30, 34") || !darkText.Contains("dark")) throw new InvalidOperationException("Dark scheme regression: " + darkText);
            Console.WriteLine("PASS: native fingerprint dark background and native color scheme; collectors not executed.");
        }
        finally { window.Content = null; await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
}
