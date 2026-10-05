using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Model;

internal static class PermissionRequestsSmoke
{
    private const string Origin = "https://native-permissions.test";
    private const string FrameOrigin = "https://native-permission-frame.test";
    private const string Media = """
        (async()=>{try{const stream=await navigator.mediaDevices.getUserMedia({video:true,audio:true});
        const live=stream.getTracks().every(track=>track.readyState==='live');stream.getTracks().forEach(track=>track.stop());
        return live?'live':'inactive';}catch(e){return e.name;}})()
        """;

    public static async Task RunAsync(Window window, string root)
    {
        // Fake devices supply a positive control; fake UI must remain disabled so native requests run.
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "native-permission-requests"),
            new() { AdditionalBrowserArguments = "--use-fake-device-for-media-stream", ExclusiveUserDataFolderAccess = true });
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        try
        {
            using var view = new WebView2();
            window.Content = view;
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.AddWebResourceRequestedFilter("https://native-permission*.test/*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) => e.Response = environment.CreateWebResourceResponse(
                new MemoryStream("<!doctype html><title>Native permission fixture</title>"u8.ToArray()), 200, "OK", "Content-Type: text/html");
            await Navigate(core, Origin + "/");
            foreach (var origin in new[] { Origin, FrameOrigin })
                foreach (var kind in new[] { CoreWebView2PermissionKind.Camera, CoreWebView2PermissionKind.Microphone, CoreWebView2PermissionKind.Geolocation })
                    await core.Profile.SetPermissionStateAsync(kind, origin, CoreWebView2PermissionState.Allow);
            if ((await Eval(core, Media)).GetString() != "live") throw new InvalidOperationException("Fake camera/microphone Allow positive control failed.");
            var denials = new List<string>();
            var config = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Native permission fixture", GraphicsPolicy = GraphicsPolicy.StrictFingerprintExperimental };
            await BrowserPermissionRequests.InstallAsync(core, config, () => true, diagnostic: message =>
            {
                denials.Add(message);
                Console.WriteLine("Native request control: " + message);
            });
            if (!BrowserPermissionRequests.IsReady(core)) throw new InvalidOperationException("Native request guard not ready.");
            var stored = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            if (stored.Any(setting => setting.PermissionState == CoreWebView2PermissionState.Allow)) throw new InvalidOperationException("Blocked native grants survived cleanup.");
            var state = await Eval(core, "navigator.permissions.query({name:'camera'}).then(p=>p.state)");
            if (state.GetString() != "prompt") throw new InvalidOperationException("Native camera prompt condition absent: " + state);
            if ((await Eval(core, Media)).GetString() != "NotAllowedError") throw new InvalidOperationException("Native camera/microphone request escaped denial.");
            var geo = await Eval(core, "new Promise(resolve=>navigator.geolocation.getCurrentPosition(()=>resolve('allowed'),e=>resolve(e.code),{timeout:3000}))");
            if (geo.GetInt32() != 1) throw new InvalidOperationException("Native geolocation request escaped denial.");
            core.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith(FrameOrigin, StringComparison.Ordinal)) return;
                var html = "<!doctype html><script>" + Media + ".then(result=>parent.postMessage(result,'" + Origin + "'));</script>";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html");
            };
            var frame = await Eval(core, "new Promise(resolve=>{addEventListener('message',e=>{if(e.origin==='" + FrameOrigin + "')resolve(e.data)},{once:true});const f=document.createElement('iframe');f.allow='camera; microphone';f.src='" + FrameOrigin + "/';document.body.append(f);})");
            if (frame.GetString() != "NotAllowedError") throw new InvalidOperationException("Cross-origin frame camera/microphone request escaped denial.");
            if (!denials.Any(s => s.EndsWith("Camera", StringComparison.Ordinal)) || !denials.Any(s => s.EndsWith("Geolocation", StringComparison.Ordinal)))
                throw new InvalidOperationException("Actual native camera and geolocation callbacks were not observed.");
            // A new controller uses the same profile. Explicit exceptions preserve compatible grants.
            using var exceptionView = new WebView2();
            window.Content = exceptionView;
            await exceptionView.EnsureCoreWebView2Async(environment);
            await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Camera, Origin, CoreWebView2PermissionState.Allow);
            await BrowserPermissionRequests.InstallAsync(exceptionView.CoreWebView2,
                config with { PrivacyExceptions = PrivacyException.Camera }, () => true);
            stored = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            if (!stored.Any(s => s.PermissionKind == CoreWebView2PermissionKind.Camera && s.PermissionState == CoreWebView2PermissionState.Allow))
                throw new InvalidOperationException("Camera exception grant was erased.");
            Console.WriteLine("PASS: native camera/microphone fake-device positive control; stored grants erased; camera=prompt with actual requests denied; geolocation denied; cross-origin frame denied; camera exception grant preserved.");
        }
        finally { window.Content = null; await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    private static async Task Navigate(CoreWebView2 core, string uri)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? sender, CoreWebView2NavigationCompletedEventArgs e) => completed.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Done;
        try { core.Navigate(uri); if (!await completed.Task.WaitAsync(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Native permission fixture did not load."); }
        finally { core.NavigationCompleted -= Done; }
    }

    private static async Task<JsonElement> Eval(CoreWebView2 core, string expression)
    {
        var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(10));
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("Native permission evaluation failed: " + json);
        return doc.RootElement.GetProperty("result").GetProperty("value").Clone();
    }
}
