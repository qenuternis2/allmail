using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Storage;

internal static class ProfileTabsSmoke
{
    private const string Home = "https://allmail-tabs-home.test/index.html";
    private const string Other = "https://allmail-tabs-other.test/index.html";

    public static async Task RunAsync(Window window, string root)
    {
        var directory = Path.Combine(root, "profile-tabs");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), """
            <!doctype html><title>Profile tabs fixture</title><script>
            globalThis.first = {
              href:location.href, timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,
              rtc:typeof RTCPeerConnection==='undefined', crypto:!!crypto.subtle,
              memory:navigator.deviceMemory, hints:typeof navigator.userAgentData==='undefined'||
                (navigator.userAgentData.brands.length===0&&navigator.userAgentData.platform===''&&navigator.userAgentData.mobile===false),
              cpu:navigator.hardwareConcurrency,
              check:typeof FontFaceSet.prototype.check==='undefined',
              metrics:typeof CanvasRenderingContext2D.prototype.measureText==='undefined'
            };
            globalThis.firstHints=typeof navigator.userAgentData==='undefined'?Promise.resolve(null):
              navigator.userAgentData.getHighEntropyValues(['architecture','bitness','formFactors','fullVersionList','model','platformVersion','uaFullVersion','wow64']);
            globalThis.readPermissions=async()=>Object.fromEntries(await Promise.all(
              ['camera','microphone','geolocation','accelerometer','gyroscope','magnetometer','midi','camera-ptz','midi-sysex','idle-detection','window-management']
              .map(async name=>[name,(await navigator.permissions.query(name==='camera-ptz'?{name:'camera',panTiltZoom:true}:name==='midi-sysex'?{name:'midi',sysex:true}:{name})).state])));
            globalThis.firstPermissions=readPermissions();
            </script><a href="https://allmail-tabs-other.test/index.html?link=1" target="_blank">Other site</a>
            """);
        var paths = new ManagedPaths(Path.Combine(directory, "data"));
        Directory.CreateDirectory(paths.Root);
        var repository = new SqliteProfileRepository(paths.DatabasePath);
        var host = new Host(window, directory);
        var engine = new WebView2Engine(host, paths, new PermissionPolicy(repository),
            new NavigationPolicy(["https://allmail-tabs-home.test"], new Uri(Home)), new InMemoryCredentialStore());
        host.Engine = engine;
        engine.PrivacyDiagnostic = observation => Console.WriteLine("Profile tabs bootstrap: " + observation);
        async Task<WebView2Session> StartAsync(bool seedLegacy = false)
        {
            var config = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Tabs fixture", GraphicsPolicy = GraphicsPolicy.StrictFingerprintExperimental, BrowserTimeZoneId = "Europe/Riga" };
            if (seedLegacy) await SeedLegacyStorageAsync(paths.UserDataFolder(config.Id), directory);
            var context = new GenerationContext(config.Id, 1);
            host.Current.Add(context);
            var session = (WebView2Session)await engine.StartAsync(new(context, config, 1, paths.UserDataFolder(config.Id), host.Current.Contains), CancellationToken.None);
            host.Sessions.Add(context, session);
            await Loaded(session.MainView!, Home);
            return session;
        }

        WebView2Session? first = null, isolated = null;
        try
        {
            first = await StartAsync(seedLegacy: true);
            var initial = first.MainView!;
            await VerifyFirstScript(initial);
            var legacyState = await Eval(initial, "({storage:localStorage.getItem('legacy-fixture'),cookie:document.cookie})");
            if (legacyState.GetProperty("storage").GetString() != "preserved" || !legacyState.GetProperty("cookie").GetString()!.Contains("legacy-fixture=preserved", StringComparison.Ordinal))
                throw new InvalidOperationException("Legacy Default migration lost existing localStorage/cookies.");
            if (!Directory.Exists(Path.Combine(paths.UserDataFolder(first.Context.ProfileId), "EBWebView", WebViewDefaultProfileMigration.BackupDirectory, "Local Storage")))
                throw new InvalidOperationException("Legacy Default backup was not retained.");
            Console.WriteLine("Profile tabs fixture: legacy cookies/localStorage migrated and backup retained.");
            await Eval(initial, "localStorage.setItem('tabs-fixture','shared');document.cookie='tabs-fixture=shared;path=/;secure';true");
            await VerifyPermissions(initial);
            var second = await engine.OpenTabAsync(first, Home + "?second=1") ?? throw new InvalidOperationException("Manual tab not created.");
            await Loaded(second, Home + "?second=1");
            await VerifyFirstScript(second);
            var state = await Eval(second, "({storage:localStorage.getItem('tabs-fixture'),cookie:document.cookie})");
            if (state.GetProperty("storage").GetString() != "shared" || !state.GetProperty("cookie").GetString()!.Contains("tabs-fixture=shared", StringComparison.Ordinal)) throw new InvalidOperationException("Same-profile tabs did not share storage/cookies.");
            var tabs = host.Tabs[first.Context];
            tabs.Select(initial);
            if (initial.Visibility != Visibility.Visible || second.Visibility != Visibility.Hidden) throw new InvalidOperationException("Selecting a tab lost its visibility/state.");
            tabs.Select(second);
            if (second.Visibility != Visibility.Visible || initial.Visibility != Visibility.Hidden || first.MainView != second) throw new InvalidOperationException("Active tab did not update.");
            // Exercise the actual +, address-bar Go, and close buttons, not a duplicate tab implementation.
            var uiCount = first.Views.Count;
            Click(tabs, "Новая вкладка (Ctrl+T)");
            await Until(() => first.Views.Count == uiCount + 1 && first.MainView is not null);
            var fromUi = first.MainView!;
            var address = Descendants(tabs).OfType<TextBox>().Single();
            address.Text = Other;
            Click(tabs, "Открыть введённый адрес");
            await Loaded(fromUi, Other);
            await VerifyFirstScript(fromUi);
            var close = Descendants(tabs).OfType<Button>().Single(b => Equals(b.ToolTip, "Закрыть вкладку (Ctrl+W)")
                && ((StackPanel)b.Parent).Children.OfType<Button>().First().FontWeight == FontWeights.SemiBold);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => first.Views.Count == uiCount);
            tabs.Select(second);
            await VerifyPermissions(second);
            address.Text = Other;
            Click(tabs, "Открыть введённый адрес");
            await Loaded(second, Other);
            await VerifyFirstScript(second);
            await Navigate(second, Home + "?history=1");
            if (!second.CoreWebView2.CanGoBack) throw new InvalidOperationException("Tab history missing.");
            await CommandNavigation(second, () => second.CoreWebView2.GoBack(), Other);
            if (!second.CoreWebView2.CanGoForward) throw new InvalidOperationException("Tab forward history missing.");
            await CommandNavigation(second, () => second.CoreWebView2.GoForward(), Home + "?history=1");
            await CommandNavigation(second, () => second.CoreWebView2.Reload(), Home + "?history=1");
            Click(tabs, "Сайт профиля");
            await Loaded(second, Home);
            Console.WriteLine("Profile tabs fixture: manual tabs, UI buttons and history passed; opening popup.");
            var count = first.Views.Count;
            await Eval(second, "window.open('https://allmail-tabs-other.test/index.html?popup=1','_blank');true", userGesture: true);
            await Until(() => first.Views.Count == count + 1 && first.MainView is not null);
            var popup = first.MainView!;
            await Loaded(popup, Other + "?popup=1");
            await VerifyFirstScript(popup);
            Console.WriteLine("Profile tabs fixture: popup first script passed; closing popup.");
            await Eval(popup, "setTimeout(()=>window.close(),20);true");
            await Until(() => first.Views.Count == count);
            await VerifyPermissions(second);
            var prior = first.Views.Count;
            try { await engine.OpenTabAsync(first, "javascript:alert(1)"); throw new InvalidOperationException("Unsafe tab address accepted."); }
            catch (ArgumentException) { }
            if (first.Views.Count != prior) throw new InvalidOperationException("Unsafe address created a controller.");
            // An independent profile must not see the previous profile's session, even on the same origin.
            isolated = await StartAsync();
            var separate = await Eval(isolated.MainView!, "({storage:localStorage.getItem('tabs-fixture'),cookie:document.cookie})");
            if (separate.GetProperty("storage").ValueKind != JsonValueKind.Null || separate.GetProperty("cookie").GetString()!.Contains("tabs-fixture=shared", StringComparison.Ordinal)) throw new InvalidOperationException("Profiles shared storage/cookies.");
            await first.CloseTabAsync(initial);
            if (first.Views.Count != 1 || first.MainView != second || first.IsClosing) throw new InvalidOperationException("Closing the original tab stopped remaining tabs.");
            state = await Eval(second, "localStorage.getItem('tabs-fixture')");
            if (state.GetString() != "shared") throw new InvalidOperationException("Closing a tab lost shared state.");
            await VerifyPermissions(second);
            await first.CloseTabAsync(second);
            await first.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));
            if (!first.IsClosing || first.Views.Count != 0 || host.Tabs.ContainsKey(first.Context) || isolated.IsClosing) throw new InvalidOperationException("Last-tab shutdown affected the wrong profile or leaked views.");
            host.Current.Remove(isolated.Context);
            if (await engine.OpenTabAsync(isolated) is not null || isolated.Views.Count != 1) throw new InvalidOperationException("Stale generation created a tab.");
            if (host.Problems.Count != 0) throw new InvalidOperationException(string.Join("; ", host.Problems));
            Console.WriteLine("PASS: profile browser tabs; production engine/UI; manual tabs and popup first-script guards; 11 permissions retained after tab/popup/initial-tab closure; legacy Default cookies/localStorage migrated with backup; shared cookies/localStorage; separate-profile isolation; tab selection/back/forward/reload; window.close; original-tab closure; last-tab process exit; unsafe URLs and stale generations rejected.");
        }
        catch (Exception e)
        {
            Console.WriteLine("Profile tabs fixture failed before cleanup: " + e);
            throw;
        }
        finally
        {
            foreach (var session in new[] { first, isolated })
                if (session is not null)
                {
                    host.Current.Remove(session.Context);
                    await session.CloseAsync();
                    await session.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));
                }
        }
    }

    private static async Task SeedLegacyStorageAsync(string userDataFolder, string fixtureDirectory)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        var legacy = new WebView2();
        var window = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000, Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Content = legacy };
        window.Show();
        try
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = WebView2Engine.BrowserProfileName;
            options.IsInPrivateModeEnabled = false;
            await legacy.EnsureCoreWebView2Async(environment, options);
            if (Path.GetFileName(legacy.CoreWebView2.Profile.ProfilePath) != WebViewDefaultProfileMigration.LegacyDirectory)
                throw new InvalidOperationException("Legacy Default layout differs from the migration fixture.");
            legacy.CoreWebView2.SetVirtualHostNameToFolderMapping("allmail-tabs-home.test", fixtureDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            legacy.CoreWebView2.Navigate(Home);
            await Loaded(legacy, Home);
            await Eval(legacy, "localStorage.setItem('legacy-fixture','preserved');true");
            var cookie = legacy.CoreWebView2.CookieManager.CreateCookie("legacy-fixture", "preserved", "allmail-tabs-home.test", "/");
            cookie.IsSecure = true;
            cookie.Expires = DateTime.UtcNow.AddDays(1);
            legacy.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
        }
        finally { window.Close(); legacy.Dispose(); }
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }

    private static async Task VerifyFirstScript(WebView2 view)
    {
        var value = await Eval(view, "first");
        if (value.GetProperty("timeZone").GetString() != "Europe/Riga" || value.GetProperty("memory").GetInt32() != 8
            || value.GetProperty("cpu").GetInt32() != UserAgentHintsBootstrap.ExpectedCpu(view.CoreWebView2)
            || new[] { "rtc", "crypto", "hints", "check", "metrics" }.Any(k => !value.GetProperty(k).GetBoolean())) throw new InvalidOperationException("New tab's first script ran without its profile guards: " + value);
        var hints = await Eval(view, "firstHints");
        if (hints.ValueKind != JsonValueKind.Null && hints.EnumerateObject().Any(p => p.Value.ValueKind switch
            { JsonValueKind.String => p.Value.GetString() != "", JsonValueKind.Array => p.Value.GetArrayLength() != 0,
                JsonValueKind.False => false, _ => true }))
            throw new InvalidOperationException("First-script UA Client Hints exposed metadata: " + hints);
        await VerifyPermissions(view, firstScript: true);
    }

    private static async Task VerifyPermissions(WebView2 view, bool firstScript = false)
    {
        var permissions = await Eval(view, firstScript ? "firstPermissions" : "readPermissions()");
        if (permissions.EnumerateObject().Count() != 11 || permissions.EnumerateObject().Any(p => p.Value.GetString() != "denied"))
            throw new InvalidOperationException("Named profile permission restrictions failed: " + permissions);
    }

    private static async Task<JsonElement> Eval(WebView2 view, string expression, bool userGesture = false)
    {
        using var document = JsonDocument.Parse(await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, returnByValue = true, awaitPromise = true, userGesture })).WaitAsync(TimeSpan.FromSeconds(8)));
        if (document.RootElement.TryGetProperty("exceptionDetails", out var error)) throw new InvalidOperationException("Tab fixture evaluation: " + error);
        return document.RootElement.GetProperty("result").GetProperty("value").Clone();
    }

    private static async Task Loaded(WebView2 view, string address)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            if (view.CoreWebView2.Source == address)
            {
                var value = await Eval(view, "typeof first==='object' ? first.href : null");
                if (value.ValueKind == JsonValueKind.String && value.GetString() == address) return;
            }
            await Task.Delay(30);
        }
        throw new TimeoutException("Tab fixture did not load " + address);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Tab state transition timed out."); await Task.Delay(30); }
    }

    private static Task Navigate(WebView2 view, string address) => CommandNavigation(view, () => view.CoreWebView2.Navigate(address), address);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Click(ProfileBrowserTabs tabs, string tooltip) => Descendants(tabs).OfType<Button>()
        .Single(b => Equals(b.ToolTip, tooltip)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task CommandNavigation(WebView2 view, Action action, string address)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) => ready.TrySetResult(e.IsSuccess);
        view.CoreWebView2.NavigationCompleted += Completed;
        try { action(); if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(12))) throw new InvalidOperationException("Tab navigation failed."); await Loaded(view, address); }
        finally { view.CoreWebView2.NavigationCompleted -= Completed; }
    }

    private sealed class Host : IBrowserViewHost
    {
        private readonly Grid _root = new();
        private readonly string _directory;
        public Dictionary<GenerationContext, ProfileBrowserTabs> Tabs { get; } = [];
        public Dictionary<GenerationContext, WebView2Session> Sessions { get; } = [];
        public HashSet<GenerationContext> Current { get; } = [];
        public List<string> Problems { get; } = [];
        public WebView2Engine? Engine { get; set; }
        public Host(Window window, string directory) { _directory = directory; window.Content = _root; }
        public void Attach(GenerationContext context, WebView2 view)
        {
            if (!Tabs.TryGetValue(context, out var tabs))
            {
                tabs = new ProfileBrowserTabs(context, Home); Tabs.Add(context, tabs); _root.Children.Add(tabs);
                tabs.NewTabRequested += async () => { if (Engine is not null && Sessions.TryGetValue(context, out var session)) await Engine.OpenTabAsync(session); };
                tabs.CloseTabRequested += async closing => { if (Sessions.TryGetValue(context, out var session)) await session.CloseTabAsync(closing); };
            }
            foreach (var control in Tabs.Values) control.Visibility = control == tabs ? Visibility.Visible : Visibility.Hidden;
            tabs.Add(view);
            view.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                view.CoreWebView2.SetVirtualHostNameToFolderMapping("allmail-tabs-home.test", _directory, CoreWebView2HostResourceAccessKind.Allow);
                view.CoreWebView2.SetVirtualHostNameToFolderMapping("allmail-tabs-other.test", _directory, CoreWebView2HostResourceAccessKind.Allow);
            };
        }
        public void Detach(GenerationContext context, WebView2 view)
        {
            if (!Tabs.TryGetValue(context, out var tabs)) return;
            tabs.Remove(view);
            if (tabs.Count == 0) { _root.Children.Remove(tabs); Tabs.Remove(context); }
        }
        public void TabReady(GenerationContext context, WebView2 view) => Tabs[context].MarkReady(view);
        public WebView2? ActiveView(GenerationContext context) => Tabs.GetValueOrDefault(context)?.ActiveView;
        public Task<UserPermissionAnswer?> AskPermissionAsync(GenerationContext context, string origin, PermissionKindKey kind) => Task.FromResult<UserPermissionAnswer?>(null);
        public void OfferExternalLink(GenerationContext context, string uri) => throw new InvalidOperationException("Web tab unexpectedly offered an external browser.");
        public Task<string?> ChooseDownloadPathAsync(GenerationContext context, string sanitizedFileName, string? initialDirectory) => Task.FromResult<string?>(null);
        public void ReportDownload(DownloadInfo info) { }
        public void ReportProblem(GenerationContext context, string message) { Problems.Add(message); Console.WriteLine("Profile tabs engine problem: " + message); }
        public async Task StopProfileAsync(GenerationContext context, string message)
        {
            Current.Remove(context);
            if (Sessions.TryGetValue(context, out var session)) await session.CloseAsync();
            else throw new InvalidOperationException("Profile stopped during initialization: " + message);
        }
    }
}
