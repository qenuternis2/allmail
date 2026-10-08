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
        await File.WriteAllTextAsync(Path.Combine(directory, "isolation-sw.js"), """
            self.addEventListener('install',e=>e.waitUntil(self.skipWaiting()));
            self.addEventListener('activate',e=>e.waitUntil(self.clients.claim()));
            self.addEventListener('message',e=>e.waitUntil((async()=>{
              const c=await caches.open('worker-isolation');
              if(e.data.write) await c.put('/worker-marker',new Response(e.data.write));
              e.ports[0].postMessage(await (await c.match('/worker-marker'))?.text()??null);
            })()));
            """);
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
        async Task<WebView2Session> StartAsync(bool seedLegacy = false, bool autoTimeZone = false, Guid? profileId = null, long generation = 1, GraphicsPolicy graphics = GraphicsPolicy.StrictFingerprintExperimental, bool reputationChecking = true)
        {
            var config = new ProfileConfig { Id = profileId ?? Guid.NewGuid(), DisplayName = "Tabs fixture", GraphicsPolicy = graphics,
                BrowserTimeZoneId = autoTimeZone ? null : "Europe/Riga", BrowserTimeZoneAuto = autoTimeZone,
                TrackingPreventionLevel = TrackingPreventionLevel.Strict, ReputationCheckingEnabled = reputationChecking };
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

        WebView2Session? first = null, isolated = null, automatic = null, promptFallback = null, saved = null, restored = null, storageA = null, storageB = null;
        try
        {
            automatic = await StartAsync(autoTimeZone: true, reputationChecking: false);
            RequireReputationChecking(automatic, false);
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
            first = await StartAsync(seedLegacy: true, reputationChecking: false);
            RequireReputationChecking(first, false);
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
            address.Text = "javascript:alert(1)";
            Click(tabs, "Открыть введённый адрес");
            tabs.UpdateLayout();
            var addressError = Descendants(tabs).OfType<TextBlock>().Single(t => t.Text == "Введите HTTP/HTTPS адрес сайта.");
            if (!addressError.IsVisible) throw new InvalidOperationException("Invalid-address message hidden by modern tab chrome.");
            address.Text = Other;
            Click(tabs, "Открыть введённый адрес");
            await Loaded(fromUi, Other);
            if (addressError.IsVisible) throw new InvalidOperationException("Empty tab status still occupies space after successful navigation.");
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
            RequireReputationChecking(first, false);
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
            RequireReputationChecking(isolated, true);
            RequireReputationChecking(first, false);
            using (var probe = new WebView2())
            {
                var probeWindow = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000,
                    ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Content = probe };
                probeWindow.Show();
                try
                {
                    var error = await engine.InitializeProbeViewAsync(first, probe, _ => { });
                    if (error is not null || probe.CoreWebView2.Settings.IsReputationCheckingRequired)
                        throw new InvalidOperationException("Fingerprint probe changed the disabled SmartScreen setting: " + error);
                    RequireReputationChecking(first, false);
                    RequireReputationChecking(isolated, true);
                }
                finally { probeWindow.Close(); }
            }
            Console.WriteLine("PASS: SmartScreen disabled in main, manual/UI/popup tabs, retained permission controller and fingerprint probe; another profile remains enabled; automatic GeoIP bootstrap retains preference.");
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
            saved = await StartAsync(reputationChecking: false);
            var savedId = saved.Context.ProfileId;
            var retained = await engine.OpenTabAsync(saved, Other + "?saved=1#part") ?? throw new InvalidOperationException("Session tab not created.");
            await Loaded(retained, Other + "?saved=1#part");
            var blank = await engine.OpenTabAsync(saved) ?? throw new InvalidOperationException("Blank tab not created.");
            var doomed = await engine.OpenTabAsync(saved, Home + "?closed=1") ?? throw new InvalidOperationException("Middle-click tab not created.");
            await Loaded(doomed, Home + "?closed=1");
            var savedUi = host.Tabs[saved.Context];
            var closedDrag = savedUi.CreateTabDragData(doomed);
            savedUi.Select(retained);
            // Raise the same preview event delivered by a wheel click, on the inactive tab title.
            var doomedTitle = Descendants(savedUi).OfType<Button>().Single(b => Equals(b.ToolTip, Home + "?closed=1"));
            var middle = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            doomedTitle.RaiseEvent(middle);
            await Until(() => saved.Views.Count == 3);
            if (!middle.Handled || saved.MainView != retained) throw new InvalidOperationException("Middle-click did not close the inactive tab without switching selection.");
            savedUi.UpdateLayout();
            var rootView = saved.Views[0];
            var rootCore = rootView.CoreWebView2;
            await rootCore.ExecuteScriptAsync("globalThis.reorderSentinel = 73");
            var rootTitle = Descendants(savedUi).OfType<Button>().Single(b => Equals(b.ToolTip, Home));
            var rootHeader = (Border)((Grid)rootTitle.Parent).Parent;
            var add = Descendants(savedUi).OfType<Button>().Single(b => Equals(b.Content, "+"));
            // Exercise production routed drop handlers: inactive tab to the end, then blank to the front.
            DropTab(add, savedUi.CreateTabDragData(rootView), new Point(2, 2), DragDropEffects.Move);
            if (!saved.Views.SequenceEqual(new[] { retained, blank, rootView }) || saved.MainView != retained)
                throw new InvalidOperationException("End drop changed selection or lost session ordering.");
            var retainedTitle = Descendants(savedUi).OfType<Button>().Single(b => Equals(b.ToolTip, Other + "?saved=1#part"));
            var retainedHeader = (Border)((Grid)retainedTitle.Parent).Parent;
            DropTab(retainedHeader, savedUi.CreateTabDragData(blank), new Point(1, 2), DragDropEffects.Move);
            // Active tab also moves without a controller replacement; dropping on its right half appends after it.
            DropTab(rootHeader, savedUi.CreateTabDragData(retained), new Point(rootHeader.ActualWidth - 1, 2), DragDropEffects.Move);
            if (!saved.Views.SequenceEqual(new[] { blank, rootView, retained }) || saved.MainView != retained)
                throw new InvalidOperationException("Active tab drop did not preserve the live controller.");
            DropTab(rootHeader, savedUi.CreateTabDragData(retained), new Point(1, 2), DragDropEffects.Move);
            DropTab(add, host.Tabs[isolated.Context].CreateTabDragData(isolated.Views[0]), new Point(2, 2), DragDropEffects.None);
            DropTab(add, closedDrag, new Point(2, 2), DragDropEffects.None);
            DropTab(add, new DataObject(DataFormats.Text, "https://example.com/"), new Point(2, 2), DragDropEffects.None);
            if (!saved.Views.SequenceEqual(new[] { blank, retained, rootView }) || saved.MainView != retained
                || rootView.CoreWebView2 != rootCore || await rootCore.ExecuteScriptAsync("globalThis.reorderSentinel") != "73")
                throw new InvalidOperationException("Reordering reloaded a page or accepted a foreign/closed/external tab.");
            var strip = (StackPanel)rootHeader.Parent;
            if (strip.Children[0] != ((Grid)Descendants(savedUi).OfType<Button>().Single(b => Equals(b.ToolTip, "about:blank")).Parent).Parent
                || strip.Children[1] != retainedHeader || strip.Children[2] != rootHeader || strip.Children[3] != add)
                throw new InvalidOperationException("Visual tab order diverged from the saved order or displaced the add button.");
            Console.WriteLine("PASS: native WPF tab drag/drop; before/after/end insertion; active and inactive tabs keep selection and live page; cross-profile, closed-tab and external drops rejected; visual/session ordering synchronized.");
            await saved.CloseAsync();
            await saved.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));
            host.Current.Remove(saved.Context);
            var snapshot = new BrowserTabsStore(paths).Load(savedId)!;
            if (!snapshot.Addresses.SequenceEqual(new[] { "about:blank", Other + "?saved=1#part", Home }) || snapshot.ActiveIndex != 1)
                throw new InvalidOperationException("Profile shutdown lost tab order, blank tab, URL fragment or active selection.");
            restored = await StartAsync(profileId: savedId, generation: 2, reputationChecking: false);
            RequireReputationChecking(restored, false);
            await Loaded(restored.Views[1], Other + "?saved=1#part");
            await Loaded(restored.Views[2], Home);
            await Until(() => restored.Views[0].CoreWebView2.Source == "about:blank");
            if (restored.Views.Count != 3 || restored.MainView != restored.Views[1]) throw new InvalidOperationException("Restart failed to restore all tabs or the active selection.");
            await VerifyFirstScript(restored.Views[2]);
            await VerifyFirstScript(restored.Views[1]);
            await VerifyPermissions(restored.Views[1]);
            Console.WriteLine("PASS: middle-click inactive tab closes without switching; profile restart restores three ordered tabs including blank and URL fragment, active selection and first-script privacy; explicitly closed tabs excluded; last-tab close saves empty session.");
            storageA = await StartAsync(graphics: GraphicsPolicy.RuntimeDefault);
            storageB = await StartAsync(graphics: GraphicsPolicy.RuntimeDefault);
            const string storageScript = """
                globalThis.writeIsolation=async value=>{
                  document.cookie='isolation='+value+';path=/;secure;max-age=3600'; localStorage.setItem('isolation',value);
                  await new Promise((resolve,reject)=>{const r=indexedDB.open('isolation',1);
                    r.onupgradeneeded=()=>r.result.createObjectStore('values');
                    r.onerror=()=>reject(r.error);r.onsuccess=()=>{const db=r.result,t=db.transaction('values','readwrite');
                    t.objectStore('values').put(value,'marker');t.oncomplete=()=>{db.close();resolve();};t.onerror=()=>reject(t.error);};});
                  const c=await caches.open('isolation');await c.put('/marker',new Response(value));return true;
                };
                globalThis.readIsolation=async()=>({cookie:document.cookie,local:localStorage.getItem('isolation'),
                  cache:await (await (await caches.open('isolation')).match('/marker'))?.text()??null,
                  indexed:await new Promise((resolve,reject)=>{const r=indexedDB.open('isolation',1);
                    r.onupgradeneeded=()=>r.result.createObjectStore('values');r.onerror=()=>reject(r.error);
                    r.onsuccess=()=>{const db=r.result,t=db.transaction('values');const g=t.objectStore('values').get('marker');
                    g.onsuccess=()=>resolve(g.result??null);t.oncomplete=()=>db.close();};})});
                globalThis.channel=new BroadcastChannel('isolation');globalThis.messages=[];channel.onmessage=e=>messages.push(e.data);
                globalThis.workerMarker=async value=>{await navigator.serviceWorker.register('/isolation-sw.js');
                  const r=await navigator.serviceWorker.ready;return new Promise(resolve=>{const c=new MessageChannel();
                  c.port1.onmessage=e=>{c.port1.close();resolve(e.data);};r.active.postMessage({write:value},[c.port2]);});}; true;
                """;
            foreach(var session in new[]{storageA,storageB}) await Eval(session.MainView!,storageScript);
            await Eval(storageA.MainView!, "writeIsolation('A')"); await Eval(storageB.MainView!, "writeIsolation('B')");
            await RequireStorage(storageA.MainView!, "A"); await RequireStorage(storageB.MainView!, "B");
            if((await Eval(storageA.MainView!,"workerMarker('A')")).GetString()!="A" ||
               (await Eval(storageB.MainView!,"workerMarker('B')")).GetString()!="B" ||
               (await Eval(storageA.MainView!,"workerMarker(null)")).GetString()!="A") throw new InvalidOperationException("Service worker storage crossed profiles.");
            var peer=await engine.OpenTabAsync(storageA,Home+"?storage-peer=1") ?? throw new InvalidOperationException("Storage peer tab missing.");
            await Loaded(peer,Home+"?storage-peer=1"); await Eval(peer,storageScript);
            await Eval(storageA.Views[0],"channel.postMessage('A');true");
            for(var attempt=0; !(await Eval(peer,"messages.includes('A')")).GetBoolean();attempt++) { if(attempt>=100) throw new TimeoutException("Broadcast positive control missing.");await Task.Delay(25); }
            await Task.Delay(250);
            if((await Eval(storageB.MainView!,"messages.length")).GetInt32()!=0) throw new InvalidOperationException("BroadcastChannel crossed profiles.");
            var storageId=storageA.Context.ProfileId; await storageA.CloseAsync();await storageA.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));host.Current.Remove(storageA.Context);
            storageA=await StartAsync(profileId:storageId,generation:2,graphics:GraphicsPolicy.RuntimeDefault);
            await Eval(storageA.MainView!,storageScript);await RequireStorage(storageA.MainView!,"A");await RequireStorage(storageB.MainView!,"B");
            if((await Eval(storageA.MainView!,"workerMarker(null)")).GetString()!="A") throw new InvalidOperationException("Service worker state lost on restart.");
            var storageBId=storageB.Context.ProfileId;await storageB.CloseAsync();await storageB.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));host.Current.Remove(storageB.Context);
            storageB=await StartAsync(profileId:storageBId,generation:2,graphics:GraphicsPolicy.RuntimeDefault);
            await Eval(storageB.MainView!,storageScript);await RequireStorage(storageB.MainView!,"B");await RequireStorage(storageA.MainView!,"A");
            if((await Eval(storageB.MainView!,"workerMarker(null)")).GetString()!="B") throw new InvalidOperationException("B service worker state lost on restart.");
            Console.WriteLine("PASS: production profile isolation and restart: cookies, LocalStorage, IndexedDB, CacheStorage; Service Worker cache A/B and persistence; BroadcastChannel same-profile positive control and cross-profile negative control.");
            foreach(var session in new[]{first,isolated,automatic,promptFallback,saved,restored,storageA,storageB})
                if(session is not null) {await session.CloseAsync();await session.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));host.Sessions.Remove(session.Context);host.Current.Remove(session.Context);}
            var lifecycle=new ProfileLifecycleService(repository,engine,credentials,paths);
            var ids=Enumerable.Range(0,4).Select(i=>{var c=ProfileStartPage.WithUrl(new ProfileConfig {Id=Guid.NewGuid(),DisplayName="Lifecycle fixture "+i},Home);repository.Insert(c);return c.Id;}).ToArray();
            for(var i=0;i<3;i++) if((await lifecycle.OpenAsync(ids[i])).Outcome!=OpenOutcome.Opened)throw new InvalidOperationException("Real lifecycle did not open three profiles.");
            if((await lifecycle.OpenAsync(ids[3])).Outcome!=OpenOutcome.CapacityReached || lifecycle.LiveProfiles().Count!=3)throw new InvalidOperationException("Real lifecycle silently exceeded capacity.");
            var otherInstance=new ProfileLifecycleService(repository,engine,credentials,paths);
            if((await otherInstance.OpenAsync(ids[0])).Outcome!=OpenOutcome.LockedElsewhere)throw new InvalidOperationException("Second instance acquired a live profile.");
            var crashTabs=host.Tabs.Single(p=>p.Key.ProfileId==ids[0]).Value;
            await Loaded(crashTabs.ActiveView!,Home);
            await crashTabs.ActiveView!.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {expression="window.open('"+Home+"?crash-child=1','_blank');true",userGesture=true}));
            await Until(()=>crashTabs.Count==2);await Loaded(crashTabs.ActiveView!,Home+"?crash-child=1");
            var victimPid=lifecycle.GetState(ids[0]).BrowserProcessId!.Value;
            var marker=Path.Combine(paths.UserDataFolder(ids[0]),"crash-marker");File.WriteAllText(marker,"preserved");
            using(var victim=System.Diagnostics.Process.GetProcessById(victimPid)) victim.Kill(); // Exact owned fixture PID only.
            await Until(()=>lifecycle.GetState(ids[0]).Phase==LifecyclePhase.Closed);
            await Until(()=>!host.Tabs.Keys.Any(c=>c.ProfileId==ids[0]));
            if(lifecycle.GetState(ids[1]).Phase!=LifecyclePhase.Open || File.ReadAllText(marker)!="preserved")throw new InvalidOperationException("A browser crash affected B or removed A data.");
            if((await lifecycle.OpenAsync(ids[0])).Outcome!=OpenOutcome.Opened)throw new InvalidOperationException("Real crash recovery failed.");
            foreach(var id in ids.Take(3)) await lifecycle.CloseAsync(id);
            Console.WriteLine("PASS: real production lifecycle: three environments, fourth rejected, second instance rejected; exact A browser PID crash disposes old controllers, preserves UDF, leaves B open and recovers A.");
            var samples=new List<string>{"cycle,openHostAndRuntimeWorkingSetBytes,openRuntimeProcesses,closedHostAndRuntimeWorkingSetBytes,closedRuntimeProcesses,hostManagedBytes,liveProfileEnvironments,retainedClosedSessions"};
            var closedSessions=new List<WeakReference>();
            for(var cycle=1;cycle<=20;cycle++) {
                if((await lifecycle.OpenAsync(ids[0])).Outcome!=OpenOutcome.Opened)throw new InvalidOperationException("Cycle open failed.");
                var pid=lifecycle.GetState(ids[0]).BrowserProcessId!.Value;
                closedSessions.Add(new WeakReference(lifecycle.GetSession(ids[0])));
                var family=NativeProcessFamily.Capture(pid);
                try {
                var openedMemory=NativeProcessFamily.Measure(family);
                using var openedHost=System.Diagnostics.Process.GetCurrentProcess();openedHost.Refresh();var openTotal=openedHost.WorkingSet64+openedMemory.Bytes;
                if((await lifecycle.CloseAsync(ids[0])).Outcome!=CloseOutcome.Closed)throw new InvalidOperationException("Cycle close failed.");
                try {using var process=System.Diagnostics.Process.GetProcessById(pid);if(!process.HasExited)throw new InvalidOperationException("Closed cycle retained owned browser PID.");} catch(ArgumentException) { }
                await Until(()=>NativeProcessFamily.Measure(family).Count==0);
                await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Keep the STA message pump running while COM/WPF finalizers release resources.
                await Task.Run(()=>{GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();});
                await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                using var current=System.Diagnostics.Process.GetCurrentProcess();current.Refresh();
                var retainedSessions=closedSessions.Count(reference=>reference.IsAlive);
                samples.Add($"{cycle},{openTotal},{openedMemory.Count},{current.WorkingSet64},0,{GC.GetTotalMemory(false)},{lifecycle.LiveProfiles().Count},{retainedSessions}");
                Directory.CreateDirectory("artifacts/test-results");await File.WriteAllLinesAsync("artifacts/test-results/lifecycle-memory.csv",samples);
                if(retainedSessions>1)throw new InvalidOperationException("Closed profile sessions accumulated across lifecycle cycles: "+retainedSessions);
                } catch {
                    Console.WriteLine("Cycle "+cycle+" surviving captured processes: "+string.Join("; ",family.Select(p=>p.Process).Where(p=>!p.HasExited).Select(p=>$"{p.Id} {p.ProcessName} born={p.StartTime:O} workingSet={p.WorkingSet64}")));
                    throw;
                } finally { foreach(var process in family)process.Dispose(); }
            }
            Directory.CreateDirectory("artifacts/test-results");await File.WriteAllLinesAsync("artifacts/test-results/lifecycle-memory.csv",samples);
            Console.WriteLine("PASS: 20 production native lifecycle cycles; BrowserProcessExited awaited each cycle, owned browser/descendant PIDs absent and live environment count zero; aggregate host/Runtime memory samples: "+string.Join("; ",samples.Skip(1)));
            WebView2 CurrentView(Guid id)=>host.Tabs.Single(p=>p.Key.ProfileId==id).Value.ActiveView ?? throw new InvalidOperationException("Lifecycle active view missing.");
            var catalog=new ProtonProfiles.Core.ProfileCatalog(repository,credentials);
            catalog.SaveSettings(repository.Get(ids[1])! with {ColorScheme=ColorSchemePreference.Light,
                LanguageMode=LanguageMode.Custom,LanguageTag="fr-FR",ScriptLocaleMode=ScriptLocaleMode.Custom,ScriptLocaleTag="fr-FR"},false);
            await lifecycle.OpenAsync(ids[0]);await lifecycle.OpenAsync(ids[1]);
            // Crash recovery/cycles correctly restored the formerly active popup URL.
            // Observe settings at a deliberate common page rather than assuming the home tab is active.
            await Navigate(CurrentView(ids[0]),Home);await Navigate(CurrentView(ids[1]),Home);
            const string settingsObservation="({ua:navigator.userAgent,language:navigator.language,locale:Intl.DateTimeFormat().resolvedOptions().locale,dark:matchMedia('(prefers-color-scheme: dark)').matches})";
            var nativeDefault=await Eval(CurrentView(ids[0]),settingsObservation);var untouchedB=await Eval(CurrentView(ids[1]),settingsObservation);
            using var languageReceiver=new UaHintsServer();
            async Task<string> WireLanguage(Guid id)
            {
                var headers=await Eval(CurrentView(id),"fetch("+JsonSerializer.Serialize(languageReceiver.Uri+"echo")+",{credentials:'omit'}).then(r=>{if(!r.ok)throw new Error(r.status);return r.json();})");
                var header=headers.EnumerateObject().SingleOrDefault(p=>p.Name.Equals("Accept-Language",StringComparison.OrdinalIgnoreCase));
                if(header.Value.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(header.Value.GetString()))
                    throw new InvalidOperationException("HTTP receiver did not observe Accept-Language: "+headers.GetRawText());
                return header.Value.GetString()!;
            }
            static void RequireLanguage(string header,string language)
            {
                var firstRange=header.Split(',')[0].Split(';')[0].Trim();
                if(!firstRange.Equals(language,StringComparison.OrdinalIgnoreCase)&&!firstRange.StartsWith(language+"-",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected first Accept-Language range: "+header+"; expected "+language);
            }
            var defaultWireA=await WireLanguage(ids[0]);var unchangedWireB=await WireLanguage(ids[1]);
            RequireLanguage(unchangedWireB,"fr");
            await Eval(CurrentView(ids[0]),"localStorage.setItem('ua-session','preserved');document.cookie='ua-session=preserved;path=/;secure;max-age=3600';true");
            var custom=repository.Get(ids[0])! with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="FixtureBrowser/1.0",LanguageMode=LanguageMode.Custom,LanguageTag="de-DE",ScriptLocaleMode=ScriptLocaleMode.Custom,ScriptLocaleTag="de-DE",ColorScheme=ColorSchemePreference.Dark,ZoomFactor=1.25};
            if(!catalog.SaveSettings(custom,true).RestartRequired || (await lifecycle.RestartAsync(ids[0])).Outcome!=OpenOutcome.Opened)throw new InvalidOperationException("Settings restart failed.");
            await Loaded(CurrentView(ids[0]),Home);var changed=await Eval(CurrentView(ids[0]),settingsObservation);
            var customWireA=await WireLanguage(ids[0]);RequireLanguage(customWireA,"de");
            if(await WireLanguage(ids[1])!=unchangedWireB)throw new InvalidOperationException("A language restart changed B's HTTP language.");
            // Runtime display-language fallback can expose de for the requested de-DE.
            // ScriptLocale independently preserves the explicit regional Intl locale.
            if(changed.GetProperty("ua").GetString()!="FixtureBrowser/1.0" || changed.GetProperty("language").GetString() is not ("de-DE" or "de") || changed.GetProperty("locale").GetString()!="de-DE" || !changed.GetProperty("dark").GetBoolean() || CurrentView(ids[0]).ZoomFactor!=1.25 || (await Eval(CurrentView(ids[1]),settingsObservation)).GetRawText()!=untouchedB.GetRawText() || CurrentView(ids[1]).ZoomFactor!=1)
                throw new InvalidOperationException("Per-profile UA/language/script locale/theme/zoom mismatch: "+changed.GetRawText());
            catalog.SaveSettings(repository.Get(ids[0])! with {UserAgentMode=UserAgentMode.Default,CustomUserAgent=null,LanguageMode=LanguageMode.System,LanguageTag=null,ScriptLocaleMode=ScriptLocaleMode.Default,ScriptLocaleTag=null,ColorScheme=ColorSchemePreference.Auto,ZoomFactor=1},true);
            if((await lifecycle.RestartAsync(ids[0])).Outcome!=OpenOutcome.Opened)throw new InvalidOperationException("Restore defaults restart failed.");
            await Loaded(CurrentView(ids[0]),Home);
            var defaults=await Eval(CurrentView(ids[0]),settingsObservation);
            var restoredWireA=await WireLanguage(ids[0]);
            if(restoredWireA!=defaultWireA || await WireLanguage(ids[1])!=unchangedWireB)
                throw new InvalidOperationException("HTTP language restore/isolation failed: "+defaultWireA+" -> "+customWireA+" -> "+restoredWireA);
            Console.WriteLine("PASS: actual HTTP Accept-Language receiver; A System="+defaultWireA+", Custom de-DE="+customWireA+", restored="+restoredWireA+"; B fr-FR="+unchangedWireB+" unchanged across A restarts.");
            Console.WriteLine("Native settings observations: requested browser language de-DE / ScriptLocale de-DE; custom="+changed.GetRawText()+"; restored="+defaults.GetRawText()+"; untouched B="+untouchedB.GetRawText());
            var preservedSession=await Eval(CurrentView(ids[0]),"({local:localStorage.getItem('ua-session'),cookie:document.cookie})");
            var finalB=await Eval(CurrentView(ids[1]),settingsObservation);
            if(defaults.GetRawText()!=nativeDefault.GetRawText() || CurrentView(ids[0]).ZoomFactor!=1 || preservedSession.GetProperty("local").GetString()!="preserved" || !preservedSession.GetProperty("cookie").GetString()!.Contains("ua-session=preserved",StringComparison.Ordinal) || finalB.GetRawText()!=untouchedB.GetRawText())
                throw new InvalidOperationException("Restore defaults mismatch: baseline="+nativeDefault.GetRawText()+"; restored="+defaults.GetRawText()+"; zoom="+CurrentView(ids[0]).ZoomFactor+"; session="+preservedSession.GetRawText()+"; baseline B="+untouchedB.GetRawText()+"; final B="+finalB.GetRawText());
            foreach(var id in ids.Take(2))await lifecycle.CloseAsync(id);
            Console.WriteLine("PASS: production Custom UA -> Runtime Default; native language/Intl locale, dark/light theme and zoom A/B isolation; explicit restart/revisions; restored defaults and cookies/LocalStorage preserved.");
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
            foreach (var session in new[] { first, isolated, automatic, promptFallback, saved, restored, storageA, storageB })
                if (session is not null)
                {
                    host.Current.Remove(session.Context);
                    await session.CloseAsync();
                    await session.ProcessExited.WaitAsync(TimeSpan.FromSeconds(12));
                }
        }
    }

    private static void RequireReputationChecking(WebView2Session session, bool enabled)
    {
        var window = (Window?)typeof(WebView2Session).GetField("_permissionWindow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session);
        if (session.Views.Count == 0 || session.Views.Any(v => v.CoreWebView2.Settings.IsReputationCheckingRequired != enabled)
            || window?.Content is not WebView2 guard || guard.CoreWebView2.Settings.IsReputationCheckingRequired != enabled)
            throw new InvalidOperationException("Profile SmartScreen setting differs across production controllers.");
    }

    private static async Task RequireStorage(WebView2 view,string marker)
    {
        var data=await Eval(view,"readIsolation()");
        if(data.GetProperty("local").GetString()!=marker || data.GetProperty("indexed").GetString()!=marker ||
           data.GetProperty("cache").GetString()!=marker || !data.GetProperty("cookie").GetString()!.Contains("isolation="+marker,StringComparison.Ordinal))
            throw new InvalidOperationException("Storage isolation mismatch: "+data.GetRawText());
    }

    private static void DropTab(UIElement target, IDataObject data, Point point, DragDropEffects expected)
    {
        // WPF constructs drag events internally; invoke that constructor only in this native fixture.
        var constructor = typeof(DragEventArgs).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)
            ?? throw new InvalidOperationException("Native WPF drag event constructor unavailable.");
        var over = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, point]);
        over.RoutedEvent = DragDrop.DragOverEvent;
        target.RaiseEvent(over);
        var drop = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, point]);
        drop.RoutedEvent = DragDrop.DropEvent;
        target.RaiseEvent(drop);
        if (!over.Handled || over.Effects != expected || !drop.Handled || drop.Effects != expected)
            throw new InvalidOperationException("Tab drop accepted or rejected the wrong payload.");
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
        if (address == "about:blank")
        {
            await Until(() => view.CoreWebView2.Source == address);
            return;
        }
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
        private WebView2? _initializingView;
        public Host(Window window, string directory) { _directory = directory; window.Content = _root; }
        public void Attach(GenerationContext context, WebView2 view)
        {
            if (!Tabs.TryGetValue(context, out var tabs))
            {
                tabs = new ProfileBrowserTabs(context, Home); Tabs.Add(context, tabs); _root.Children.Add(tabs);
                tabs.NewTabRequested += async () => { if (Engine is not null && Sessions.TryGetValue(context, out var session)) await Engine.OpenTabAsync(session); };
                tabs.CloseTabRequested += async closing => { if (Sessions.TryGetValue(context, out var session)) await session.CloseTabAsync(closing); };
                tabs.TabMoveRequested += (moved, index) => Current.Contains(context) && Sessions.TryGetValue(context, out var session) && session.MoveTab(moved, index);
            }
            foreach (var control in Tabs.Values) control.Visibility = control == tabs ? Visibility.Visible : Visibility.Hidden;
            tabs.Add(view);
            view.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                InitializingCore = view.CoreWebView2;
                _initializingView = view;
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
            if(ReferenceEquals(_initializingView,view)){InitializingCore=null;_initializingView=null;}
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
