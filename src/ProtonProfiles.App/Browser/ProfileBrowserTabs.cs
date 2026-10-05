using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Navigation;

namespace ProtonProfiles.App.Browser;

/// <summary>One profile's tab strip and address bar. Hidden tabs stay attached and retain their pages.</summary>
public sealed class ProfileBrowserTabs : UserControl
{
    private sealed class Tab(WebView2 view, Button select, Button close, TextBlock title)
    {
        public WebView2 View { get; } = view;
        public Button Select { get; } = select;
        public Button Close { get; } = close;
        public TextBlock Title { get; } = title;
        public Border Header { get; set; } = null!;
        public bool Ready { get; set; }
        public bool Loading { get; set; }
    }

    private readonly List<Tab> _tabs = [];
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _pages = new();
    private readonly TextBox _address = new() { Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center, MinWidth = 100 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, Margin = new Thickness(8, 2, 8, 2) };
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _reload;
    private readonly Button _add;
    private Tab? _active;
    private bool _editingAddress;

    public GenerationContext Context { get; }
    public string HomeAddress { get; set; }
    public WebView2? ActiveView => _active is { Ready: true } tab && tab.View.CoreWebView2 is not null ? tab.View : null;
    public int Count => _tabs.Count;
    public event Action? NewTabRequested;
    public event Action<WebView2>? CloseTabRequested;

    public ProfileBrowserTabs(GenerationContext context, string homeAddress)
    {
        Context = context;
        HomeAddress = homeAddress;
        var root = new DockPanel();
        var tabsBar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(239, 242, 246)) };
        _add = MakeButton("+", "Новая вкладка (Ctrl+T)", () => { NewTabRequested?.Invoke(); FocusAddress(); });
        _add.Margin = new Thickness(4, 2, 2, 2);
        _add.VerticalAlignment = VerticalAlignment.Center;
        _strip.Children.Add(_add);
        tabsBar.Children.Add(new ScrollViewer { Content = _strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        DockPanel.SetDock(tabsBar, Dock.Top);
        root.Children.Add(tabsBar);
        var navigation = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _back = MakeButton("←", "Назад (Alt+←)", () => { if (ActiveView?.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); });
        _forward = MakeButton("→", "Вперёд (Alt+→)", () => { if (ActiveView?.CoreWebView2 is { CanGoForward: true } core) core.GoForward(); });
        _reload = MakeButton("↻", "Обновить / остановить (F5)", ReloadOrStop);
        buttons.Children.Add(_back);
        buttons.Children.Add(_forward);
        buttons.Children.Add(_reload);
        buttons.Children.Add(MakeButton("⌂", "Сайт профиля", () => Navigate(HomeAddress)));
        DockPanel.SetDock(buttons, Dock.Left);
        navigation.Children.Add(buttons);
        var go = MakeButton("Перейти", "Открыть введённый адрес", () => Navigate(_address.Text));
        DockPanel.SetDock(go, Dock.Right);
        navigation.Children.Add(go);
        _address.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Адрес сайта");
        _address.ToolTip = "HTTP/HTTPS адрес (Ctrl+L). Адрес без схемы открывается через HTTPS.";
        _address.GotKeyboardFocus += (_, _) => { _editingAddress = true; _address.SelectAll(); };
        _address.LostKeyboardFocus += (_, _) => { _editingAddress = false; Refresh(); };
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Navigate(_address.Text); }
            else if (e.Key == Key.Escape) { _editingAddress = false; Refresh(); ActiveView?.Focus(); e.Handled = true; }
        };
        navigation.Children.Add(_address);
        DockPanel.SetDock(navigation, Dock.Top);
        root.Children.Add(navigation);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_pages);
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            var action = Shortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
            if (action is null) return;
            e.Handled = true;
            // WebView2 forwards accelerator keys to WPF; return from its callback before browser commands.
            Dispatcher.BeginInvoke(new Action(() => Execute(action)));
        };
        Refresh();
    }

    private Button MakeButton(string text, string tooltip, Action action)
    {
        var button = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += (_, _) => Execute(action);
        return button;
    }

    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception e) { _status.Text = "Не удалось выполнить действие: " + e.Message; }
    }

    public void Add(WebView2 view)
    {
        var title = new TextBlock { Text = "Подготовка…", Width = 145, TextTrimming = TextTrimming.CharacterEllipsis };
        var select = MakeButton("", "Выбрать вкладку", () => Select(view));
        select.Content = title;
        select.Margin = new Thickness(0);
        select.BorderThickness = new Thickness(0);
        select.Background = Brushes.Transparent;
        var close = MakeButton("×", "Закрыть вкладку (Ctrl+W)", () => RequestClose(view));
        close.Margin = new Thickness(0);
        close.Padding = new Thickness(4);
        close.Width = 26;
        close.BorderThickness = new Thickness(0);
        close.Background = Brushes.Transparent;
        close.IsEnabled = false;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(select);
        Grid.SetColumn(close, 1);
        content.Children.Add(close);
        var header = new Border
        {
            Child = content, Margin = new Thickness(2, 2, 0, 2), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(177, 184, 194)), CornerRadius = new CornerRadius(3),
            ClipToBounds = true
        };
        var tab = new Tab(view, select, close, title) { Header = header };
        header.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            // Return from the mouse callback before disposing a browser controller.
            Dispatcher.BeginInvoke(new Action(() => Execute(() => RequestClose(view))));
        };
        _tabs.Add(tab);
        // The add button is the final item, immediately after the last tab, in the same scrollable row.
        _strip.Children.Insert(_strip.Children.Count - 1, header);
        _pages.Children.Add(view);
        view.Visibility = Visibility.Hidden;
        view.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess || !_tabs.Contains(tab)) return;
            var core = view.CoreWebView2;
            core.SourceChanged += (_, _) => RefreshIfPresent(tab);
            core.DocumentTitleChanged += (_, _) => RefreshIfPresent(tab);
            core.HistoryChanged += (_, _) => RefreshIfPresent(tab);
            core.NavigationStarting += (_, _) => { tab.Loading = true; _status.Text = string.Empty; RefreshIfPresent(tab); };
            core.NavigationCompleted += (_, ready) =>
            {
                if (!_tabs.Contains(tab)) return;
                tab.Loading = false;
                if (ReferenceEquals(tab, _active)) _status.Text = ready.IsSuccess || ready.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled
                    ? string.Empty : "Не удалось загрузить страницу: " + ready.WebErrorStatus;
                Refresh();
            };
        };
        Select(view);
    }

    public void MarkReady(WebView2 view)
    {
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.View, view));
        if (tab is null) return;
        tab.Ready = true;
        tab.Close.IsEnabled = true;
        Refresh();
    }

    public void Remove(WebView2 view)
    {
        var index = _tabs.FindIndex(t => ReferenceEquals(t.View, view));
        if (index < 0) return;
        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        _strip.Children.Remove(tab.Header);
        _pages.Children.Remove(view);
        if (ReferenceEquals(tab, _active)) _active = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        _editingAddress = false;
        _status.Text = string.Empty;
        Refresh();
    }

    public void Select(WebView2 view)
    {
        _active = _tabs.FirstOrDefault(t => ReferenceEquals(t.View, view));
        _editingAddress = false;
        _status.Text = string.Empty;
        Refresh();
        _active?.Header.BringIntoView();
        if (_active is not null && ReferenceEquals(_active, _tabs.LastOrDefault())) _add.BringIntoView();
    }

    public void FocusAddress() { _address.Focus(); _address.SelectAll(); }
    private void RefreshIfPresent(Tab tab) { if (_tabs.Contains(tab)) Refresh(); }

    private void Refresh()
    {
        foreach (var tab in _tabs)
        {
            var selected = ReferenceEquals(tab, _active);
            tab.View.Visibility = selected && tab.Ready ? Visibility.Visible : Visibility.Hidden;
            tab.Select.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            tab.Header.Background = selected ? Brushes.White : Brushes.LightGray;
            if (tab.Ready && tab.View.CoreWebView2 is { } core)
            {
                var text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? (core.Source == "about:blank" ? "Новая вкладка" : core.Source) : core.DocumentTitle;
                tab.Title.Text = (tab.Loading ? "… " : string.Empty) + text;
                tab.Select.ToolTip = core.Source;
            }
        }
        var active = ActiveView?.CoreWebView2;
        _back.IsEnabled = active?.CanGoBack == true;
        _forward.IsEnabled = active?.CanGoForward == true;
        _reload.IsEnabled = active is not null;
        _reload.Content = _active?.Loading == true ? "×" : "↻";
        if (!_editingAddress) _address.Text = active is null || active.Source == "about:blank" ? string.Empty : active.Source;
    }

    private void Navigate(string address)
    {
        if (!BrowserAddress.TryNormalize(address, out var normalized)) { _status.Text = "Введите HTTP/HTTPS адрес сайта."; return; }
        if (ActiveView?.CoreWebView2 is not { } core) { _status.Text = "Вкладка ещё подготавливается."; return; }
        try { _editingAddress = false; _status.Text = string.Empty; core.Navigate(normalized); ActiveView?.Focus(); }
        catch (Exception e) { _status.Text = "Не удалось открыть адрес: " + e.Message; }
    }

    private void ReloadOrStop()
    {
        if (ActiveView?.CoreWebView2 is not { } core) return;
        if (_active?.Loading == true) core.Stop(); else core.Reload();
    }

    private void RequestClose(WebView2 view)
    {
        if (_tabs.Any(t => ReferenceEquals(t.View, view) && t.Ready)) CloseTabRequested?.Invoke(view);
    }

    private Action? Shortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control)
            return key switch
            {
                Key.T => () => { NewTabRequested?.Invoke(); FocusAddress(); },
                Key.W => () => { if (ActiveView is { } view) RequestClose(view); },
                Key.L => FocusAddress,
                Key.Tab => () => Cycle(1),
                Key.R => ReloadOrStop,
                _ => null,
            };
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab) return () => Cycle(-1);
        if (modifiers == ModifierKeys.Alt && key == Key.Left) return () => _back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (modifiers == ModifierKeys.Alt && key == Key.Right) return () => _forward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return modifiers == ModifierKeys.None && key == Key.F5 ? ReloadOrStop : null;
    }

    private void Cycle(int direction)
    {
        if (_active is null || _tabs.Count < 2) return;
        Select(_tabs[(_tabs.IndexOf(_active) + direction + _tabs.Count) % _tabs.Count].View);
    }
}
