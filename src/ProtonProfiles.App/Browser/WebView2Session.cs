using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

/// <summary>One environment generation: profile tabs, auxiliary windows and the retained exit signal.</summary>
public sealed class WebView2Session : IBrowserSession
{
    private readonly CoreWebView2Environment _environment;
    private readonly IBrowserViewHost _host;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<WebView2> _views = [];
    private readonly List<Window> _auxiliary = [];
    private bool _closing;

    public GenerationContext Context { get; }
    public Task ProcessExited => _exited.Task;
    public int? BrowserProcessId { get; internal set; }
    public string? RuntimeVersion { get; internal set; }
    public ProxyAuthRetryBudget ProxyAuthBudget { get; } = new();
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
    internal bool ContainsView(WebView2 view) => _views.Contains(view);
    internal void TabReady(WebView2 view) { if (!_closing && ContainsView(view)) _host.TabReady(Context, view); }

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
        return view;
    }

    internal void RemoveView(WebView2 view)
    {
        if (!_views.Remove(view)) return;
        _host.Detach(Context, view);
        view.Dispose();
    }

    public async Task CloseTabAsync(WebView2 view)
    {
        if (_closing || !ContainsView(view)) return;
        if (_views.Count == 1) await _host.StopProfileAsync(Context, "Закрыта последняя вкладка профиля.");
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
        _closing = true;
        foreach (var w in _auxiliary.ToList())
        {
            try { w.Close(); } catch (InvalidOperationException) { }
        }
        _auxiliary.Clear();
        LogFile?.Dispose();
        LogFile = null;
        foreach (var view in _views.ToArray()) RemoveView(view);
        return Task.CompletedTask;
    }
}
