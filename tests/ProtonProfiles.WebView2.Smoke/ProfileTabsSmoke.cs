using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
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
        new GeoIpTimeZoneDatabase(paths).Install(Path.Combine(AppContext.BaseDirectory, "fixtures", "GeoIP2-City-Test.mmdb"));
        var repository = new SqliteProfileRepository(paths.DatabasePath);
        using var proxy = new GeoIpTimeZoneSmoke.IpServer(true, https: true);
        if (!ProxyEndpoint.TryParse($"http://127.0.0.1:{proxy.Port}", out var proxyEndpoint, out var proxyError)) throw new InvalidOperationException(proxyError);
        var credentials = new InMemoryCredentialStore();
        var host = new Host(window, directory);
        var engine = new WebView2Engine(host, paths, new PermissionPolicy(repository),
            new NavigationPolicy(["https://allmail-tabs-home.test"], new Uri(Home)), credentials);
        host.Engine = engine;
        Task? resetPermissions = null;
        var resetObserved = false;
        var refreshed = false;
        var persistentReset = false;
        var persistentPromptReads = 0;
        var nativeFallback = false;
        engine.PrivacyDiagnostic = observation =>
        {
            Console.WriteLine("Profile tabs bootstrap: " + observation);
            if (persistentReset && observation == "Additional privacy bootstrap: reading native observation")
                resetPermissions = host.InitializingCore!.CallDevToolsProtocolMethodAsync("Browser.resetPermissions", "{}");
            if (persistentReset && observation.StartsWith("Additional privacy secure bootstrap:", StringComparison.Ordinal)
                && observation.Contains("\"camera\":\"prompt\"", StringComparison.Ordinal)) persistentPromptReads++;
            if (persistentReset && observation.StartsWith("Additional privacy bootstrap: nonuniform permission query;", StringComparison.Ordinal)) nativeFallback = true;
            if (resetPermissions is null && observation.StartsWith("Native document defaults bootstrap:", StringComparison.Ordinal))
                resetPermissions = host.InitializingCore!.CallDevToolsProtocolMethodAsync("Browser.resetPermissions", "{}");
            if (resetPermissions is not null && observation.StartsWith("Additional privacy secure bootstrap:", StringComparison.Ordinal)
                && observation.Contains("\"camera\":\"prompt\"", StringComparison.Ordinal)) resetObserved = true;
            if (observation == "Additional privacy bootstrap: refreshing retained hardware permission guard") refreshed = true;
        };
        async Task<WebView2Session> StartAsync(bool seedLegacy = false, bool autoTimeZone = false, Guid? profileId = null, long generation = 1)
        {
            var config = new ProfileConfig { Id = profileId ?? Guid.NewGuid(), DisplayName = "Tabs fixture", GraphicsPolicy = GraphicsPolicy.StrictFingerprintExperimental,
                BrowserTimeZoneId = autoTimeZone ? null : "Europe/Riga", BrowserTimeZoneAuto = autoTimeZone,
                TrackingPreventionLevel = TrackingPreventionLevel.Strict };
            if (autoTimeZone) config = config with { NetworkMode = NetworkMode.Proxy,
                Proxy = new ProxySettings(proxyEndpoint, ProxyAuthMode.Basic, credentials.Write(config.Id, new("fixture", "fixture-secret"))) };
            if (seedLegacy) await SeedLegacyStorageAsync(paths.UserDataFolder(config.Id), directory);
            var context = new GenerationContext(config.Id, generation);
            host.Current.Add(context);
            var session = (WebView2Session)await engine.StartAsync(new(context, config, 1, paths.UserDataFolder(config.Id), host.Current.Contains), CancellationToken.None);
            host.Sessions.Add(context, session);
            await Loaded(session.Views[0], new BrowserTabsStore(paths).Load(config.Id)?.Addresses.FirstOrDefault() ?? Home);
            return session;
        }

        WebView2Session? first = null, isolated = null, automatic = null, promptFallback = null, saved = null, restored = null;
        try
        {
            automatic = await StartAsync(autoTimeZone: true);
            if (resetPermissions is null) throw new InvalidOperationException("Permission reset control did not run.");
            await resetPermissions;
            if (!resetObserved || !refreshed) throw new InvalidOperationException("Production startup did not observe and recover the permission reset.");
            if (!proxy.Headers.Any(h => h.StartsWith("CONNECT api.ipify.org:443", StringComparison.Ordinal)
                && h.Contains("Proxy-Authorization: Basic ", StringComparison.OrdinalIgnoreCase))
                || !proxy.Headers.Any(h => h.StartsWith("GET /?format=json", StringComparison.Ordinal)))
                throw new InvalidOperationException("Production auto timezone did not fetch through authenticated HTTPS CONNECT.");
            await VerifyPermissions(automatic.MainView!);
            var zone = await Eval(automatic.MainView!, "Intl.DateTimeFormat().resolvedOptions().timeZone");
            if (zone.GetString() != "Europe/London") throw new InvalidOperationException("Automatic timezone was not applied by production startup.");
            Console.WriteLine("PASS: production engine automatic timezone startup; authenticated HTTPS CONNECT; secure readback and hardware permission denials retained after IP discovery.");
            Console.WriteLine("PASS: production startup recovered a real Browser.resetPermissions before website navigation; all 11 denials verified again; retained guard owns overrides.");
            await VerifyProxyAuthentication(automatic.MainView!,proxy,host);
            persistentReset = true;
            promptFallback = await StartAsync();
            await resetPermissions!;
            if (persistentPromptReads != 3 || !nativeFallback) throw new InvalidOperationException("Persistent native camera=prompt startup condition was not reproduced and accepted by the native request guard.");
            await VerifyFirstScript(promptFallback.MainView!, uniformPermissions: false);
            var blockedGeo = await Eval(promptFallback.MainView!, "new Promise(resolve=>navigator.geolocation.getCurrentPosition(()=>resolve(0),e=>resolve(e.code),{timeout:3000}))");
            if (blockedGeo.GetInt32() != 1) throw new InvalidOperationException("Production prompt fallback allowed a real geolocation request.");
            Console.WriteLine("PASS: production startup with persistent native camera=prompt; three real permission resets; website and Web Crypto loaded with first-script guards; native request guard required; raw fingerprint query remains nonuniform.");
            persistentReset = false;
            first = await StartAsync(seedLegacy: true);
            var initial = first.MainView!;
            await VerifyFirstScript(initial);
            var legacyState = await Eval(initial, "({storage:localStorage.getItem('legacy-fixture'),cookie:document.cookie})");
            Console.WriteLine("Profile tabs migrated storage: " + legacyState.GetRawText() + "; profile=" + initial.CoreWebView2.Profile.ProfileName + "; path=" + initial.CoreWebView2.Profile.ProfilePath);
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
            CaptureTabStrip(tabs);
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
                && ((Grid)b.Parent).Children.OfType<Button>().First().FontWeight == FontWeights.SemiBold);
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
            if (new BrowserTabsStore(paths).Load(first.Context.ProfileId)!.Addresses.Length != 0)
                throw new InvalidOperationException("Explicitly closed last tab would reopen.");
            host.Current.Remove(isolated.Context);
            if (await engine.OpenTabAsync(isolated) is not null || isolated.Views.Count != 1) throw new InvalidOperationException("Stale generation created a tab.");
            saved = await StartAsync();
            var savedId = saved.Context.ProfileId;
            var retained = await engine.OpenTabAsync(saved, Other + "?saved=1#part") ?? throw new InvalidOperationException("Session tab not created.");
            await Loaded(retained, Other + "?saved=1#part");
            var blank = await engine.OpenTabAsync(saved) ?? throw new InvalidOperationException("Blank tab not created.");
            var doomed = await engine.OpenTabAsync(saved, Home + "?closed=1") ?? throw new InvalidOperationException("Middle-click tab not created.");
            await Loaded(doomed, Home + "?closed=1");
            var savedUi = host.Tabs[saved.Context];
            savedUi.Select(retained);
            // Raise the same preview event delivered by a wheel click, on the inactive tab title.
            var doomedTitle = Descendants(savedUi).OfType<Button>().Single(b => Equals(b.ToolTip, Home + "?closed=1"));
            var middle = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            doomedTitle.RaiseEvent(middle);
            await Until(() => saved.Views.Count == 3);
            if (!middle.Handled || saved.MainView != retained) throw new InvalidOperationException("Middle-click did not close the inactive tab without switching selection.");
            await saved.CloseAsync();
            await saved.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));
            host.Current.Remove(saved.Context);
            var snapshot = new BrowserTabsStore(paths).Load(savedId)!;
            if (!snapshot.Addresses.SequenceEqual(new[] { Home, Other + "?saved=1#part", "about:blank" }) || snapshot.ActiveIndex != 1)
                throw new InvalidOperationException("Profile shutdown lost tab order, blank tab, URL fragment or active selection.");
            restored = await StartAsync(profileId: savedId, generation: 2);
            await Loaded(restored.Views[1], Other + "?saved=1#part");
            await Until(() => restored.Views[2].CoreWebView2.Source == "about:blank");
            if (restored.Views.Count != 3 || restored.MainView != restored.Views[1]) throw new InvalidOperationException("Restart failed to restore all tabs or the active selection.");
            await VerifyFirstScript(restored.Views[0]);
            await VerifyFirstScript(restored.Views[1]);
            await VerifyPermissions(restored.Views[1]);
            Console.WriteLine("PASS: middle-click inactive tab closes without switching; profile restart restores three ordered tabs including blank and URL fragment, active selection and first-script privacy; explicitly closed tabs excluded; last-tab close saves empty session.");
            if (host.Problems.Count != 0) throw new InvalidOperationException(string.Join("; ", host.Problems));
            Console.WriteLine("PASS: profile browser tabs; production engine/UI; manual tabs and popup first-script guards; 11 permissions retained after tab/popup/initial-tab closure; legacy Default cookies/localStorage migrated with backup; shared cookies/localStorage; separate-profile isolation; tab selection/back/forward/reload; window.close; original-tab closure; last-tab process exit; unsafe URLs and stale generations rejected.");
        }
        catch (Exception e)
        {
            Console.WriteLine("Profile tabs fixture failed before cleanup: " + e);
            Console.WriteLine("Proxy auth fixture challenges: "+JsonSerializer.Serialize(proxy.AuthChallenges));
            Console.WriteLine("Proxy auth fixture accepted: "+JsonSerializer.Serialize(proxy.AuthAccepted));
            if (automatic is not null)
                foreach(var entry in automatic.Connections.Snapshot())
                    Console.WriteLine("Proxy auth fixture network: "+JsonSerializer.Serialize(new {entry.Host,entry.Status,entry.Error,entry.ResourceType}));
            throw;
        }
        finally
        {
            foreach (var session in new[] { first, isolated, automatic, promptFallback, saved, restored })
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
        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, new()
        {
            AdditionalBrowserArguments = BrowserArguments.Build(null, graphics: GraphicsPolicy.StrictFingerprintExperimental),
            ExclusiveUserDataFolderAccess = true,
        });
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
            var core = legacy.CoreWebView2;
            if (Path.GetFileName(legacy.CoreWebView2.Profile.ProfilePath) != WebViewDefaultProfileMigration.LegacyDirectory)
                throw new InvalidOperationException("Legacy Default layout differs from the migration fixture.");
            legacy.CoreWebView2.SetVirtualHostNameToFolderMapping("allmail-tabs-home.test", fixtureDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            legacy.CoreWebView2.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
            await UserAgentHintsBootstrap.ApplyAsync(core, new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Legacy strict fixture", GraphicsPolicy = GraphicsPolicy.StrictFingerprintExperimental });
            legacy.CoreWebView2.Navigate(Home);
            await Loaded(legacy, Home);
            Console.WriteLine("Legacy strict permissions: " + (await Eval(legacy, "readPermissions()")).GetRawText());
            await Eval(legacy, "localStorage.setItem('legacy-fixture','preserved');true");
            var cookie = legacy.CoreWebView2.CookieManager.CreateCookie("legacy-fixture", "preserved", "allmail-tabs-home.test", "/");
            cookie.IsSecure = true;
            cookie.Expires = DateTime.UtcNow.AddDays(1);
            legacy.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            await legacy.CoreWebView2.CookieManager.GetCookiesAsync(Home);
            Console.WriteLine("Legacy profile seeded storage: " + (await Eval(legacy, "({storage:localStorage.getItem('legacy-fixture'),cookie:document.cookie})")).GetRawText());
        }
        finally { window.Close(); legacy.Dispose(); }
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }

    private static async Task VerifyFirstScript(WebView2 view, bool uniformPermissions = true)
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
        if (uniformPermissions) await VerifyPermissions(view, firstScript: true);
        else
        {
            var permissions = await Eval(view, "firstPermissions");
            if (permissions.GetProperty("camera").GetString() != "prompt" || !BrowserPermissionRequests.IsReady(view.CoreWebView2))
                throw new InvalidOperationException("Prompt fallback did not retain its real query and native request guard.");
        }
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

    private static void CaptureTabStrip(ProfileBrowserTabs tabs)
    {
        tabs.UpdateLayout();
        var add = Descendants(tabs).OfType<Button>().Single(b => Equals(b.ToolTip, "Новая вкладка (Ctrl+T)"));
        var strip = (FrameworkElement)add.Parent;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(strip.ActualWidth), (int)Math.Ceiling(strip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(strip);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine("artifacts", "test-results");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, "profile-tabs-strip.png"));
        encoder.Save(output);
        Console.WriteLine("Profile tabs fixture: rendered Windows tab strip saved to artifacts/test-results/profile-tabs-strip.png.");
    }
    private static async Task CommandNavigation(WebView2 view, Action action, string address)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) => ready.TrySetResult(e.IsSuccess);
        view.CoreWebView2.NavigationCompleted += Completed;
        try { action(); if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(12))) throw new InvalidOperationException("Tab navigation failed."); await Loaded(view, address); }
        finally { view.CoreWebView2.NavigationCompleted -= Completed; }
    }

    private static async Task VerifyProxyAuthentication(WebView2 view, GeoIpTimeZoneSmoke.IpServer proxy, Host host)
    {
        const string page="https://document.allmail-auth.test/index.html";
        await CommandNavigation(view,()=>view.CoreWebView2.Navigate(page),page);
        var first=await Eval(view,"first");
        if (!first.GetProperty("crypto").GetBoolean() || !first.GetProperty("rtc").GetBoolean()
            || first.GetProperty("zone").GetString()!="Europe/London")
            throw new InvalidOperationException("First proxied website lost startup privacy or Web Crypto.");
        var results=await Eval(view,"Promise.all([0,1,2,3].map(async i=>{try{return await (await fetch('https://api'+i+'.allmail-auth.test/data',{credentials:'include'})).json()}catch(e){return {error:String(e)}}}))");
        Console.WriteLine("Proxy auth fixture parallel results: "+results.GetRawText());
        if (results.GetArrayLength()!=4 || results.EnumerateArray().Any(r=>!r.TryGetProperty("ok",out var ok)||!ok.GetBoolean()))
            throw new InvalidOperationException("Parallel cold proxy authorization failed.");
        if (proxy.AuthAccepted.Count!=5 || proxy.AuthChallenges.Count(p=>p.Key.EndsWith(".allmail-auth.test:443",StringComparison.Ordinal))!=5)
            throw new InvalidOperationException("Independent proxy challenges were not exercised.");
        var website=await Eval(view,"fetch('https://server401.allmail-auth.test/data',{credentials:'include'}).then(r=>r.status)");
        if (website.GetInt32()!=401) throw new InvalidOperationException("Website authentication was not safely cancelled.");
        var problems=host.Problems.Count;
        var rejected=await Eval(view,"fetch('https://reject.allmail-auth.test/data',{credentials:'include'}).then(()=>false,()=>true)");
        if (!rejected.GetBoolean() || proxy.AuthChallenges["reject.allmail-auth.test:443"]>4 || host.Problems.Count!=problems+1)
            throw new InvalidOperationException("Rejected proxy credentials did not stop after a bounded retry.");
        host.Problems.RemoveAt(problems); // Expected diagnostic, unlike every other fixture problem.
        var fresh=await Eval(view,"fetch('https://after-rejection.allmail-auth.test/data',{credentials:'include'}).then(r=>r.json())");
        if (!fresh.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("One rejected request exhausted unrelated proxy auth.");
        Console.WriteLine("PASS: production proxy authentication; first network page after GeoIP, four parallel independent challenges, server 401 receives no proxy credentials, rejected credentials bounded, fresh request after rejection succeeds.");
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
        public CoreWebView2? InitializingCore { get; private set; }
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
                InitializingCore = view.CoreWebView2;
                view.CoreWebView2.BasicAuthenticationRequested += (_,auth) => Console.WriteLine("Proxy auth fixture native SDK challenge: "+auth.Uri+"; "+auth.Challenge);
                view.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_,failed) =>
                {
                    using var document=JsonDocument.Parse(failed.ParameterObjectAsJson);
                    var failure=document.RootElement;
                    Console.WriteLine("Proxy auth fixture network failure: "+failure.GetProperty("errorText").GetString()
                        +"; "+(failure.TryGetProperty("corsErrorStatus",out var cors)?cors.GetRawText():""));
                };
                view.CoreWebView2.GetDevToolsProtocolEventReceiver("Fetch.authRequired").DevToolsProtocolEventReceived += (_,auth) =>
                {
                    using var document=JsonDocument.Parse(auth.ParameterObjectAsJson);
                    var challenge=document.RootElement;
                    Console.WriteLine("Proxy auth fixture native challenge: "+challenge.GetProperty("requestId").GetString()+"; "+challenge.GetProperty("authChallenge").GetRawText());
                };
                view.CoreWebView2.SetVirtualHostNameToFolderMapping("allmail-tabs-home.test", _directory, CoreWebView2HostResourceAccessKind.Allow);
                view.CoreWebView2.SetVirtualHostNameToFolderMapping("allmail-tabs-other.test", _directory, CoreWebView2HostResourceAccessKind.Allow);
                // Ephemeral local TLS fixture only; production never bypasses certificate errors.
                view.CoreWebView2.ServerCertificateErrorDetected += (_, certificate) =>
                {
                    var host=new Uri(certificate.RequestUri).Host;
                    if (host is "api.ipify.org" or "api6.ipify.org" || host.EndsWith(".allmail-auth.test",StringComparison.Ordinal))
                        certificate.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                };
            };
        }
        public void Detach(GenerationContext context, WebView2 view)
        {
            if (!Tabs.TryGetValue(context, out var tabs)) return;
            tabs.Remove(view);
            if (tabs.Count == 0) { _root.Children.Remove(tabs); Tabs.Remove(context); }
        }
        public void TabReady(GenerationContext context, WebView2 view) => Tabs[context].MarkReady(view);
        public void SelectTab(GenerationContext context, WebView2 view) => Tabs[context].Select(view);
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
