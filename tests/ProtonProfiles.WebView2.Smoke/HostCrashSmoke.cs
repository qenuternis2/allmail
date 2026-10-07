using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

/// <summary>Terminate an owned WPF host process, not just its browser PID. Never use user data.</summary>
internal static class HostCrashSmoke
{
    private sealed record Ready(int HostId, int BrowserId, Guid ProfileId);
    private const string Marker = "host-crash-preserved";

    public static async Task RunAsync(string root, string runtimeVersion, bool delayExit = false)
    {
        var paths = new ManagedPaths(Path.Combine(root, delayExit ? "host-recovery" : "host-crash"));
        paths.EnsureBaseDirectories();
        using var site = new GeoIpTimeZoneSmoke.IpServer(false, html: true);
        var repository = new SqliteProfileRepository(paths.DatabasePath, paths.BackupsRoot);
        var a = ProfileStartPage.WithUrl(new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Crash A" }, $"http://127.0.0.1:{site.Port}/index");
        var b = a with { Id = Guid.NewGuid(), DisplayName = "Surviving B" };
        repository.Insert(a); repository.Insert(b);
        await using var survivor = new Shell(paths, runtimeVersion);
        survivor.Window.Show();
        Require((await survivor.Lifecycle.OpenAsync(b.Id)).Outcome == OpenOutcome.Opened, "B opened");
        await Loaded(survivor.View(b.Id));
        await Eval(survivor.View(b.Id), "localStorage.setItem('crash-marker','B');true");
        var bPid = survivor.Lifecycle.GetState(b.Id).BrowserProcessId;
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No harness process path.");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in new[] { delayExit ? "--host-recovery-fixture" : "--host-crash-fixture", paths.Root, a.Id.ToString("D") }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Fixture child did not start.");
        // Drain both streams immediately: production diagnostics must not block the child on a full pipe.
        var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
        NativeProcessFamily.OwnedProcess[] family = [];
        try
        {
            var readyPath = Path.Combine(paths.Root, "child-ready.json");
            await UntilAsync(() => Task.FromResult(File.Exists(readyPath) || child.HasExited), delayExit ? 65 : 15);
            if (child.HasExited) throw new InvalidOperationException("Crash fixture failed before ready: " + await errors + await output);
            var ready = JsonSerializer.Deserialize<Ready>(await File.ReadAllTextAsync(readyPath))!;
            Require(ready.HostId == child.Id && ready.ProfileId == a.Id, "ready belongs to the exact child/profile");
            family = NativeProcessFamily.Capture(ready.BrowserId);
            Require(NativeProcessFamily.Measure(family).Count > 0, "child browser processes observed before crash");
            if (!delayExit) Require((await survivor.Lifecycle.OpenAsync(a.Id)).Outcome == OpenOutcome.LockedElsewhere, "second app instance rejected while owner lives");
            var bBefore = repository.Get(b.Id);
            if (delayExit)
            {
                var marker = Path.Combine(paths.UserDataFolder(a.Id), "crash-marker.txt");
                using (new NativeProcessSuspension(family.Single(p => p.Process.Id == ready.BrowserId)))
                {
                    child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Require(NativeProcessFamily.Measure(family).Count > 0, "Runtime really outlived the crashed host");
                    var acquired = ProfileLock.TryAcquire(paths, a.Id, out var unsafeLock); unsafeLock?.Dispose();
                    Require(!acquired, "orphan Runtime blocks UDF ownership even after host OS handles are released");
                    Require((await survivor.Lifecycle.OpenAsync(a.Id)).Outcome == OpenOutcome.Blocked, "pending cleanup cannot reopen A");
                    var blocked = await survivor.Lifecycle.ResumePendingOperationsAsync();
                    Require(blocked.Count == 2 && blocked.All(r => !r.Report.Completed), "reset/delete remain pending while orphan Runtime lives");
                    Require(repository.Get(a.Id) is not null && File.ReadAllText(marker) == Marker, "orphan Runtime data was not partially deleted");
                    Require(survivor.Lifecycle.GetState(b.Id).Phase == LifecyclePhase.Open
                        && survivor.Lifecycle.GetState(b.Id).BrowserProcessId == bPid && repository.Get(b.Id) == bBefore
                        && (await Eval(survivor.View(b.Id), "localStorage.getItem('crash-marker')")).GetString() == "B", "recovery did not affect B");
                }
                await Until(() => NativeProcessFamily.Measure(family).Count == 0);
                var resumed = await survivor.Lifecycle.ResumePendingOperationsAsync();
                Require(resumed.Count == 2 && resumed.All(r => r.Report.Completed) && repository.Get(a.Id) is null
                    && !Directory.Exists(paths.ProfileDirectory(a.Id)) && repository.Get(b.Id) == bBefore, "cleanup resumes only after actual Runtime exit");
                Console.WriteLine("PASS: real default 15-second exit timeout; RecoveryRequired retains UDF/lock and denies reopen/reset/delete; host crash with suspended owned Runtime; persisted ownership blocks cleanup until natural browser/descendant exit; B unchanged; pending reset/delete resume safely.");
                return;
            }
            // Kill only the exact child host. Browser/renderer processes must exit themselves.
            child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => NativeProcessFamily.Measure(family).Count == 0);
            Require(ProfileLock.TryAcquire(paths, a.Id, out var releasedLock), "OS released the crashed host's profile lock");
            releasedLock!.Dispose();
            Require(File.ReadAllText(Path.Combine(paths.UserDataFolder(a.Id), "crash-marker.txt")) == Marker, "crash did not remove UDF");
            Require((await survivor.Lifecycle.OpenAsync(a.Id)).Outcome == OpenOutcome.Opened, "profile reopens after actual host crash");
            await Loaded(survivor.View(a.Id));
            var restored = await Eval(survivor.View(a.Id), "({local:localStorage.getItem('crash-marker'),cookie:document.cookie})");
            Require(restored.GetProperty("local").GetString() == Marker
                && restored.GetProperty("cookie").GetString()!.Contains("crash-marker=" + Marker, StringComparison.Ordinal), "persistent session data restored after host crash");
            Require(survivor.Lifecycle.GetState(b.Id).Phase == LifecyclePhase.Open
                && survivor.Lifecycle.GetState(b.Id).BrowserProcessId == bPid && repository.Get(b.Id) == bBefore
                && (await Eval(survivor.View(b.Id), "localStorage.getItem('crash-marker')")).GetString() == "B", "B process/metadata/storage unchanged");
            Console.WriteLine("PASS: actual child WPF host process crash; duplicate instance rejected before crash; captured Runtime descendants exited without kill; OS lock released; same A UDF/cookie/LocalStorage restored; unrelated live B unchanged.");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            Directory.CreateDirectory("artifacts/test-results");
            await File.WriteAllTextAsync("artifacts/test-results/" + (delayExit ? "host-recovery-child.log" : "host-crash-child.log"), await output + await errors);
            foreach (var owned in family) owned.Dispose();
        }
    }

    public static int RunChild(string root, Guid profileId, bool delayExit = false)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ProtonProfiles.WebView2.Smoke;component/Themes/Modern.xaml", UriKind.Relative) });
        var paths = new ManagedPaths(root);
        var shell = new Shell(paths, CoreWebView2Environment.GetAvailableBrowserVersionString());
        var exitCode = 1;
        WebView2? retainedController = null;
        Window? retainedWindow = null;
        shell.Window.Loaded += async (_, _) =>
        {
            try
            {
                Require((await shell.Lifecycle.OpenAsync(profileId)).Outcome == OpenOutcome.Opened, "child production lifecycle opened A");
                await Loaded(shell.View(profileId));
                await Eval(shell.View(profileId), "localStorage.setItem('crash-marker','" + Marker + "');document.cookie='crash-marker=" + Marker + ";path=/;max-age=3600';true");
                await File.WriteAllTextAsync(Path.Combine(paths.UserDataFolder(profileId), "crash-marker.txt"), Marker);
                if (delayExit)
                {
                    var session = (WebView2Session)shell.Lifecycle.GetSession(profileId)!;
                    // Deliberately retain an independent native controller in this exact environment.
                    // No fake ProcessExited task: it keeps the actual Runtime alive after production CloseAsync.
                    retainedController = new WebView2();
                    retainedWindow = new Window { Content = retainedController, ShowInTaskbar = false, Left = -10000, Top = -10000 };
                    retainedWindow.Show();
                    await retainedController.EnsureCoreWebView2Async(session.Environment, session.ControllerOptions!);
                    Require(retainedController.CoreWebView2.BrowserProcessId == session.BrowserProcessId, "delay controller uses A Runtime");
                    var wait = Stopwatch.StartNew();
                    Require((await shell.Lifecycle.CloseAsync(profileId)).Outcome == CloseOutcome.RecoveryRequired
                        && wait.Elapsed >= ProfileLifecycleService.DefaultExitTimeout
                        && shell.Lifecycle.GetState(profileId).Phase == LifecyclePhase.RecoveryRequired
                        && !session.ProcessExited.IsCompleted, "real default exit timeout enters recovery");
                    Require((await shell.Lifecycle.OpenAsync(profileId)).Outcome == OpenOutcome.RecoveryRequired, "reopen denied in recovery");
                    Require(!(await shell.Lifecycle.ResetLocalSessionAsync(profileId)).Completed
                        && !(await shell.Lifecycle.DeleteLocalProfileAsync(profileId)).Completed
                        && !session.ProcessExited.IsCompleted, "real reset/delete wait for retained native exit signal");
                    Require(File.ReadAllText(Path.Combine(paths.UserDataFolder(profileId), "crash-marker.txt")) == Marker, "recovery preserves data");
                    var acquired = ProfileLock.TryAcquire(paths, profileId, out var unsafeLock); unsafeLock?.Dispose();
                    Require(!acquired, "recovery retains app profile lock");
                    Console.WriteLine("PASS: child real Runtime kept alive by independent native controller; default 15s timeout and blocked reopen/reset/delete, retained signal/lock/UDF.");
                }
                var ready = new Ready(Environment.ProcessId, shell.Lifecycle.GetState(profileId).BrowserProcessId!.Value, profileId);
                var readyPath = Path.Combine(paths.Root, "child-ready.json");
                await File.WriteAllTextAsync(readyPath + ".tmp", JsonSerializer.Serialize(ready));
                File.Move(readyPath + ".tmp", readyPath);
                // Remain on the STA message pump until the parent terminates this host.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                retainedController?.Dispose(); retainedWindow?.Close();
                await shell.DisposeAsync(); application.Shutdown(exitCode);
            }
        };
        application.Run(shell.Window);
        return exitCode;
    }

    private sealed class Shell : IAsyncDisposable
    {
        private readonly MailfudGeoIpUpdater _updater;
        public MainWindow Window { get; }
        public ProfileLifecycleService Lifecycle { get; }
        public Shell(ManagedPaths paths, string runtimeVersion)
        {
            var repository = new SqliteProfileRepository(paths.DatabasePath, paths.BackupsRoot);
            var credentials = new InMemoryCredentialStore(); var permissions = new PermissionPolicy(repository);
            _updater = new MailfudGeoIpUpdater(paths);
            Window = new MainWindow(paths, repository, new ProfileCatalog(repository, credentials), credentials, permissions, runtimeVersion, _updater)
                { ShowInTaskbar = false, Left = -10000, Top = -10000 };
            Window.Initialize(new WebView2Engine(Window, paths, permissions, new NavigationPolicy(), credentials));
            Lifecycle = (ProfileLifecycleService)typeof(MainWindow).GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        }
        public WebView2 View(Guid id) => ((WebView2Session)Lifecycle.GetSession(id)!).Views[0];
        public async ValueTask DisposeAsync()
        {
            foreach (var profile in Lifecycle.LiveProfiles())
                Require((await Lifecycle.CloseAsync(profile.ProfileId)).Outcome == CloseOutcome.Closed, "fixture profile fully closed");
            Window.Close(); _updater.Dispose();
        }
    }

    private static async Task Loaded(WebView2 view) => await UntilAsync(async () =>
        await view.CoreWebView2.ExecuteScriptAsync("document.readyState==='complete'&&document.title==='A18 fixture'") == "true");
    private static Task Until(Func<bool> condition) => UntilAsync(() => Task.FromResult(condition()));
    private static async Task UntilAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Host crash fixture transition timed out."); await Task.Delay(25); }
    }
    private static async Task<JsonElement> Eval(WebView2 view, string expression)
    {
        var json = await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("Host crash evaluation failed: " + json);
        return document.RootElement.GetProperty("result").GetProperty("value").Clone();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Host crash regression: " + message);
    }
}
