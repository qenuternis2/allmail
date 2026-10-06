using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.App.Browser;

/// <summary>One environment generation: profile tabs, auxiliary windows and the retained exit signal.</summary>
public sealed class WebView2Session : IBrowserSession
{
    private readonly CoreWebView2Environment _environment;
    private readonly IBrowserViewHost _host;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<WebView2> _views = [];
    private readonly Dictionary<WebView2, List<BrowserDownloadTracker>> _downloads = [];
    private readonly Dictionary<WebView2, BrowserDownloadIdentity> _downloadIdentities = [];
    private readonly Dictionary<WebView2, Dictionary<Guid, Task<string?>>> _downloadChoices = [];
    private readonly Dictionary<CoreWebView2, WebView2> _controllers = [];
    private readonly List<Window> _auxiliary = [];
    private Window? _permissionWindow;
    private Window? _downloadWindow;
    private readonly Grid _downloadPages = new();
    private readonly HashSet<WebView2> _backgroundViews = [];
    private readonly Dictionary<WebView2, int> _pendingDownloads = [];
    internal Func<Task<WebView2?>>? CreateEmptyTabAsync { get; set; }
    internal int BackgroundDownloadViewCount => _backgroundViews.Count;
    private bool _closing;
    private readonly HashSet<WebView2> _readyViews = [];
    private readonly Dictionary<WebView2, string> _tabAddresses = [];
    internal AuthenticatedProxyRelay? ProxyRelay { get; set; }
    internal BrowserTabsStore? TabsStore { get; set; }
    internal bool StartupCompleted { get; set; }

    public GenerationContext Context { get; }
    public Task ProcessExited => _exited.Task;
    public int? BrowserProcessId { get; internal set; }
    public string? RuntimeVersion { get; internal set; }
    public WebView2? MainView => _host.ActiveView(Context);
    public IReadOnlyList<WebView2> Views => _views.ToArray();
    public CoreWebView2BrowserProcessExitKind? ExitKind { get; private set; }

    /// <summary>Connections of every view of this generation, for the user's local diagnostics window.</summary>
    public ConnectionLog Connections { get; } = new();
    public ConnectionLogFile? LogFile { get; internal set; }

    internal CoreWebView2Environment Environment => _environment;
    internal CoreWebView2ControllerOptions? ControllerOptions { get; set; }
    internal BrowserStartRequest? Request { get; set; }
    internal ProfileConfig? Config { get; set; }
    internal GeoIpTimeZoneResolution? AutoTimeZone { get; set; }
    public bool IsClosing => _closing;
    public int WebRtcGuardRegistrations { get; internal set; }
    public bool WebRtcGuardFailed { get; internal set; }
    public WebRtcReadbackSummary WebRtcReadback { get; internal set; } = WebRtcReadbackSummary.Empty;
    public PageGuardReadbackSummary AudioReadback { get; internal set; } = PageGuardReadbackSummary.Empty;
    public int AudioGuardRegistrations { get; internal set; }
    public bool AudioGuardFailed { get; internal set; }

    /// <summary>Registration/readback in documents is not evidence of coverage in every Runtime context.</summary>
    public string WebRtcStatusText =>
        (Config?.WebRtcPagePolicy == WebRtcPagePolicy.Allow ? "WebRTC разрешён для совместимости. "
            : WebRtcGuardFailed ? "Проверка блокировки WebRTC не пройдена; профиль закрывается. "
            : $"Блокировка WebRTC зарегистрирована в {WebRtcGuardRegistrations} окнах; полное покрытие не проверено. ")
        + (WebRtcReadback.Unavailable > 0 ? "Некоторые документы не удалось проверить; это не подтверждает отсутствие утечек. " : string.Empty)
        + (Config?.WebRtcNetworkPolicy == WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental
            ? "Ограничение UDP вне прокси настроено экспериментально; сетевое действие не проверено. "
            : "Сетевые ограничения WebRTC не настроены. ")
        + (Config is not null && AudioPageGuard.IsEnabled(Config.GraphicsPolicy)
            ? AudioGuardFailed ? "Проверка блокировки Web Audio не пройдена. "
                : $"Блокировка Web Audio зарегистрирована в {AudioGuardRegistrations} окнах; полное покрытие не проверено. " : string.Empty)
        + "Независимая сетевая защита не установлена. Среда выполнения: " + (RuntimeVersion ?? "не определена");

    public WebView2Session(GenerationContext context, CoreWebView2Environment environment, IBrowserViewHost host)
    {
        Context = context;
        _environment = environment;
        _host = host;
        // Subscribed at creation so shutdown cannot race the subscription (spec §4.4).
        _environment.BrowserProcessExited += OnBrowserProcessExited;
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        ExitKind = e.BrowserProcessExitKind;
        _environment.BrowserProcessExited -= OnBrowserProcessExited;
        _exited.TrySetResult();
    }

    internal void SetMainView(WebView2 view) => _views.Add(view);
    internal void RegisterController(WebView2 view)
    {
        var core = view.CoreWebView2;
        _controllers.Add(core, view);
        core.SourceChanged += (_, _) => RememberAddress(view, core.Source);
        core.NavigationStarting += (_, e) => { if (!e.Cancel) RememberAddress(view, e.Uri); };
    }
    private void RememberAddress(WebView2 view, string? address)
    {
        if (!_closing && _readyViews.Contains(view) && address is not null
            && BrowserTabsSnapshot.Create([address], 0).Addresses is [var normalized]) _tabAddresses[view] = normalized;
    }
    internal WebView2? FindView(CoreWebView2 core) => _controllers.GetValueOrDefault(core);
    internal bool ContainsView(WebView2 view) => _views.Contains(view);
    internal bool OwnsDownloadView(WebView2 view) => ContainsView(view) || _backgroundViews.Contains(view);
    private bool HasDownloads(WebView2 view) => _pendingDownloads.GetValueOrDefault(view) > 0
        || _downloads.TryGetValue(view, out var downloads) && downloads.Count > 0;
    internal void BeginDownload(WebView2 view) => _pendingDownloads[view] = _pendingDownloads.GetValueOrDefault(view) + 1;
    internal void EndDownload(WebView2 view)
    {
        if (_pendingDownloads.GetValueOrDefault(view) <= 1) _pendingDownloads.Remove(view);
        else _pendingDownloads[view]--;
        ReleaseDownloadViewLater(view);
    }
    private void ReleaseDownloadViewLater(WebView2 view)
    {
        // Native completion callbacks/deferrals must return before disposing their controller.
        _ = view.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_backgroundViews.Contains(view) && !HasDownloads(view)) DisposeView(view);
        }), DispatcherPriority.Background);
    }
    internal void TabReady(WebView2 view)
    {
        if (!_closing && ContainsView(view))
        {
            _readyViews.Add(view);
            RememberAddress(view, view.CoreWebView2.Source);
            _host.TabReady(Context, view);
        }
    }

    /// <summary>
    /// Browser.setPermission is owned by its DevTools session; closing that session clears all overrides for
    /// its BrowserContext. Keep one private controller alive until every user/diagnostic tab has closed.
    /// </summary>
    internal async Task InitializePermissionGuardAsync(ProfileConfig config, Action<string>? diagnostic)
    {
        if (!AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy) || _permissionWindow is not null || _closing) return;
        var view = new WebView2();
        var window = new Window { Title = "All Mails", Width = 1, Height = 1, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Content = view, WindowStyle = WindowStyle.None };
        _permissionWindow = window;
        async Task StopAfterGuardLossAsync()
        {
            if (!_closing && Request is { } request && request.IsCurrentGeneration(Context))
            {
                await CloseAsync();
                await _host.StopProfileAsync(Context, "Контроллер защиты разрешений закрыт; профиль остановлен.");
            }
        }
        window.Closed += (_, _) =>
        {
            view.Dispose();
            if (ReferenceEquals(_permissionWindow, window)) _permissionWindow = null;
            if (!_closing) window.Dispatcher.BeginInvoke(new Action(async () => await StopAfterGuardLossAsync()));
        };
        window.Show();
        await view.EnsureCoreWebView2Async(_environment, ControllerOptions);
        if (_closing) return;
        var core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, e) => e.Cancel = e.Uri != "about:blank";
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => { e.Handled = true; e.SavesInProfile = false; e.State = CoreWebView2PermissionState.Deny; };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.ProcessFailed += (_, _) =>
        {
            if (!_closing) window.Dispatcher.BeginInvoke(new Action(async () => await StopAfterGuardLossAsync()));
        };
        await RefreshHardwarePermissionsAsync(config, diagnostic);
    }

    internal Task RefreshHardwarePermissionsAsync(ProfileConfig config, Action<string>? diagnostic)
    {
        if (!AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy)) return Task.CompletedTask;
        if (_closing || _permissionWindow?.Content is not WebView2 { CoreWebView2: { } core })
            throw new InvalidOperationException("Контроллер защиты разрешений недоступен; открытие заблокировано.");
        return BrowserHardwarePermissions.ApplyAsync(core, config, diagnostic);
    }

    /// <summary>Creates an unnavigated child bound to the same environment and browser profile (S18).</summary>
    internal async Task<WebView2?> CreateTabViewAsync(CoreWebView2ControllerOptions controllerOptions)
    {
        if (_closing) return null;
        var view = new WebView2 { Visibility = Visibility.Hidden };
        _views.Add(view);
        _host.Attach(Context, view);
        try { await view.EnsureCoreWebView2Async(_environment, controllerOptions); }
        catch { RemoveView(view); throw; }
        if (_closing || !ContainsView(view)) { RemoveView(view); return null; }
        try { BrowserWindowCloseHandling.UseTabOwnership(view); RegisterController(view); }
        catch { RemoveView(view); throw; }
        return view;
    }

    internal void RegisterDownload(WebView2 view, BrowserDownloadTracker tracker)
    {
        if (!_downloads.TryGetValue(view, out var downloads)) _downloads[view] = downloads = [];
        downloads.Add(tracker);
        tracker.Stopped += () =>
        {
            downloads.Remove(tracker);
            if (_downloadChoices.TryGetValue(view, out var choices)) choices.Remove(tracker.DownloadId);
            ReleaseDownloadViewLater(view);
        };
    }

    internal async Task InitializeDownloadsAsync(CoreWebView2 core)
    {
        var view = _controllers[core];
        var identity = new BrowserDownloadIdentity(core);
        try { await identity.EnableAsync(); }
        catch { identity.Dispose(); throw; }
        if (_closing || !_views.Contains(view)) { identity.Dispose(); return; }
        _downloadIdentities.Add(view, identity);
    }
    internal BrowserDownloadTracker? FindResumingDownload(WebView2 view, Guid? id) =>
        id is not null && _downloads.TryGetValue(view, out var downloads) ? downloads.LastOrDefault(d => d.DownloadId == id) : null;
    internal Task<Guid?> IdentifyDownloadAsync(WebView2 view, string uri) => _downloadIdentities.TryGetValue(view, out var identity)
        ? identity.TakeAsync(uri, id => FindResumingDownload(view, id) is not null || _downloadChoices.TryGetValue(view, out var choices) && choices.ContainsKey(id)) : Task.FromResult<Guid?>(null);
    internal Task<string?> ChooseDownloadPathAsync(WebView2 view, Guid? id, Func<Task<string?>> choose)
    {
        if (id is null) return choose();
        if (!_downloadChoices.TryGetValue(view, out var choices)) _downloadChoices[view] = choices = [];
        if (choices.TryGetValue(id.Value, out var pending)) return pending;
        var result = new TaskCompletionSource<string?>(); choices[id.Value] = result.Task;
        async Task Resolve() { try { result.TrySetResult(await choose()); } catch (Exception e) { result.TrySetException(e); } }
        _ = Resolve(); return result.Task;
    }
    internal void DownloadStarted(WebView2 view) => RememberAddress(view, view.CoreWebView2.Source);

    internal void RemoveView(WebView2 view)
    {
        if (!_views.Remove(view)) return;
        _readyViews.Remove(view);
        _tabAddresses.Remove(view);
        if (!_closing && HasDownloads(view))
        {
            // The native download belongs to this profile, even after its tab disappears.
            _backgroundViews.Add(view);
            _host.Detach(Context, view);
            if (_downloadWindow is null)
            {
                _downloadWindow = new Window { Title = "All Mails", Width = 1, Height = 1,
                    Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
                    Opacity = 0, WindowStyle = WindowStyle.None, Content = _downloadPages };
                _downloadWindow.Show();
            }
            view.Visibility = Visibility.Hidden;
            _downloadPages.Children.Add(view);
            return;
        }
        _host.Detach(Context, view);
        DisposeView(view);
    }

    private void DisposeView(WebView2 view)
    {
        _backgroundViews.Remove(view);
        _downloadPages.Children.Remove(view);
        _pendingDownloads.Remove(view);
        if (_downloadIdentities.Remove(view, out var identity)) identity.Dispose();
        _downloadChoices.Remove(view);
        if (_downloads.Remove(view, out var downloads))
            foreach (var download in downloads.ToArray()) download.Close("Профиль закрыт.");
        foreach (var core in _controllers.Where(pair => ReferenceEquals(pair.Value, view)).Select(pair => pair.Key).ToArray())
            _controllers.Remove(core);
        view.Dispose();
        if (_backgroundViews.Count == 0 && _downloadWindow is { } window)
        {
            _downloadWindow = null;
            window.Content = null;
            window.Close();
        }
    }

    public bool MoveTab(WebView2 view, int index)
    {
        if (_closing || !_readyViews.Contains(view) || index < 0 || index >= _views.Count) return false;
        var oldIndex = _views.IndexOf(view);
        if (oldIndex < 0) return false;
        _views.RemoveAt(oldIndex);
        _views.Insert(index, view);
        return true;
    }

    public async Task CloseTabAsync(WebView2 view)
    {
        if (_closing || !ContainsView(view)) return;
        if (_views.Count == 1 && _views.Concat(_backgroundViews).Any(HasDownloads))
        {
            // Keep the profile, proxy and downloads alive when the last visible tab closes.
            if (CreateEmptyTabAsync is null || await CreateEmptyTabAsync() is null) return;
            if (_closing || !ContainsView(view)) return;
            RemoveView(view);
        }
        else if (_views.Count == 1)
        {
            // Explicitly closing the last tab must not resurrect it on the next profile launch.
            _readyViews.Remove(view);
            await _host.StopProfileAsync(Context, "Закрыта последняя вкладка профиля.");
        }
        else RemoveView(view);
    }

    /// <summary>A window holding extra controllers of this environment (diagnostics); closed before the environment shuts down.</summary>
    internal void RegisterAuxiliaryWindow(Window window)
    {
        _auxiliary.Add(window);
        window.Closed += (_, _) => _auxiliary.Remove(window);
    }

    /// <summary>Closes children and controllers and disposes the WPF controls. Exit is awaited by the lifecycle service.</summary>
    public Task CloseAsync()
    {
        if (_closing) return Task.CompletedTask;
        _closing = true;
        if (StartupCompleted && TabsStore is not null)
        {
            var views = _views.Where(_readyViews.Contains).ToArray();
            var addresses = views.Select(view => _tabAddresses.GetValueOrDefault(view, "about:blank"));
            try { TabsStore.Save(Context.ProfileId, BrowserTabsSnapshot.Create(addresses, MainView is { } active ? Array.IndexOf(views, active) : 0)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { _host.ReportProblem(Context, "Не удалось сохранить вкладки; предыдущий список сохранён: " + e.Message); }
        }
        foreach (var w in _auxiliary.ToList())
        {
            try { w.Close(); } catch (InvalidOperationException) { }
        }
        _auxiliary.Clear();
        LogFile?.Dispose();
        LogFile = null;
        foreach (var view in _views.ToArray()) RemoveView(view);
        foreach (var view in _backgroundViews.ToArray()) DisposeView(view);
        if (_permissionWindow is { } permissionWindow)
        {
            _permissionWindow = null;
            permissionWindow.Close();
        }
        ProxyRelay?.Dispose();
        ProxyRelay = null;
        return Task.CompletedTask;
    }
}
