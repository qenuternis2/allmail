using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;

namespace ProtonProfiles.App.Browser;

/// <summary>One environment generation: main view, child windows and the retained exit signal.</summary>
public sealed class WebView2Session : IBrowserSession
{
    private readonly CoreWebView2Environment _environment;
    private readonly IBrowserViewHost _host;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Window> _children = [];
    private readonly List<Window> _auxiliary = [];
    private WebView2? _main;
    private bool _closing;

    public GenerationContext Context { get; }
    public Task ProcessExited => _exited.Task;
    public int? BrowserProcessId { get; internal set; }
    public string? RuntimeVersion { get; internal set; }
    public ProxyAuthRetryBudget ProxyAuthBudget { get; } = new();
    public WebView2? MainView => _main;
    public CoreWebView2BrowserProcessExitKind? ExitKind { get; private set; }

    /// <summary>Connections of every view of this generation, for the user's local diagnostics window.</summary>
    public ConnectionLog Connections { get; } = new();
    public ConnectionLogFile? LogFile { get; internal set; }

    internal CoreWebView2Environment Environment => _environment;
    internal CoreWebView2ControllerOptions? ControllerOptions { get; set; }
    internal BrowserStartRequest? Request { get; set; }
    internal ProfileConfig? Config { get; set; }
    public bool IsClosing => _closing;

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

    internal void SetMainView(WebView2 view) => _main = view;

    /// <summary>Creates an unnavigated child bound to the same environment and browser profile (S18).</summary>
    internal async Task<WebView2?> CreateChildWindowAsync(CoreWebView2ControllerOptions controllerOptions)
    {
        if (_closing) return null;
        var view = new WebView2();
        var owner = _main is null ? null : Window.GetWindow(_main);
        var window = new Window
        {
            Title = "Proton Mail",
            Width = 900,
            Height = 700,
            Content = view,
            Owner = owner,
            ShowInTaskbar = true,
        };
        _children.Add(window);
        window.Closed += (_, _) =>
        {
            _children.Remove(window);
            view.Dispose();
        };
        window.Show();
        await view.EnsureCoreWebView2Async(_environment, controllerOptions);
        if (_closing) { window.Close(); return null; }
        view.CoreWebView2.WindowCloseRequested += (_, _) => window.Close();
        view.CoreWebView2.DocumentTitleChanged += (_, _) => window.Title = view.CoreWebView2.DocumentTitle;
        return view;
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
        foreach (var w in _children.ToList())
        {
            try { w.Close(); } catch (InvalidOperationException) { }
        }
        _children.Clear();
        foreach (var w in _auxiliary.ToList())
        {
            try { w.Close(); } catch (InvalidOperationException) { }
        }
        _auxiliary.Clear();
        LogFile?.Dispose();
        LogFile = null;
        if (_main is not null)
        {
            _host.Detach(Context, _main);
            _main.Dispose();
            _main = null;
        }
        return Task.CompletedTask;
    }
}
