using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Diagnostics;

namespace ProtonProfiles.App.Dialogs;

/// <summary>
/// Per-profile local diagnostics: live connection trace, per-host summary and the IP/fingerprint check run inside the
/// profile. Closed automatically when the profile's browser generation closes.
/// </summary>
public sealed class ProfileDiagnosticsWindow : Window
{
    private readonly WebView2Session _session;
    private readonly WebView2Engine _engine;
    private readonly string _logDirectory;
    private readonly ObservableCollection<ConnectionEntry> _rows = [];
    private readonly ICollectionView _view;
    private readonly TextBox _filter = new() { Width = 260, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Фильтр по хосту, адресу, IP, статусу или ошибке" };
    private readonly CheckBox _errorsOnly = new() { Content = "Только ошибки", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly CheckBox _autoScroll = new() { Content = "Автопрокрутка", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _counter = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray };
    private readonly DataGrid _grid;
    private readonly DataGrid _hosts;
    private readonly DispatcherTimer _hostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TabControl _tabs = new();
    private readonly TabItem _hostsTab;
    private readonly TabItem _probeTab;
    private readonly DockPanel _probePanel = new();
    private readonly TextBlock _probeStatus = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray };
    private WebView2? _probe;
    private string? _lastReport;
    private bool _probeStarted;

    public ProfileDiagnosticsWindow(Window owner, string profileName, WebView2Session session, WebView2Engine engine, string logDirectory)
    {
        _session = session;
        _engine = engine;
        _logDirectory = logDirectory;
        Owner = owner;
        Title = $"Журнал и отпечаток — «{profileName}»";
        Width = 1200;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;

        // ---- Connections tab ----
        foreach (var e in session.Connections.Snapshot()) _rows.Add(e);
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Matches;
        _grid = CreateGrid();
        _grid.ItemsSource = _view;
        _grid.Columns.Add(Col("Время", nameof(ConnectionEntry.TimeText), 90));
        _grid.Columns.Add(Col("Итог", nameof(ConnectionEntry.StatusText), 70));
        _grid.Columns.Add(Col("Метод", nameof(ConnectionEntry.Method), 60));
        _grid.Columns.Add(Col("Тип", nameof(ConnectionEntry.ResourceType), 80));
        _grid.Columns.Add(Col("Хост", nameof(ConnectionEntry.Host), 170));
        _grid.Columns.Add(Col("Удалённый адрес", nameof(ConnectionEntry.RemoteEndpoint), 150));
        _grid.Columns.Add(Col("Протокол", nameof(ConnectionEntry.Protocol), 70));
        _grid.Columns.Add(Col("TLS", nameof(ConnectionEntry.TlsText), 150));
        _grid.Columns.Add(Col("Размер", nameof(ConnectionEntry.SizeText), 70));
        _grid.Columns.Add(Col("Длит.", nameof(ConnectionEntry.DurationText), 65));
        _grid.Columns.Add(Col("URL", nameof(ConnectionEntry.Url), 380));
        _grid.Columns.Add(Col("Подробности", nameof(ConnectionEntry.Details), 400));

        _filter.TextChanged += (_, _) => RefreshView();
        _errorsOnly.Checked += (_, _) => RefreshView();
        _errorsOnly.Unchecked += (_, _) => RefreshView();
        var bar = new WrapPanel { Margin = new Thickness(8) };
        bar.Children.Add(new TextBlock { Text = "Фильтр:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(_filter);
        bar.Children.Add(_errorsOnly);
        bar.Children.Add(_autoScroll);
        bar.Children.Add(Btn("Очистить", (_, _) => _session.Connections.Clear()));
        bar.Children.Add(Btn("Сохранить…", (_, _) => SaveLog()));
        bar.Children.Add(Btn("Папка журналов", (_, _) => OpenLogFolder()));
        bar.Children.Add(_counter);
        var note = new TextBlock
        {
            Margin = new Thickness(8, 0, 8, 6),
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Text = "«Удалённый адрес» — IP сервера, с которым соединился браузер; при работе через прокси здесь будет адрес прокси. " +
                   "Значения параметров в URL скрыты, потому что в них бывают одноразовые токены. Журнал хранится только на этом компьютере " +
                   "в папке профиля и удаляется вместе с профилем; в отчёт «Диагностика…» он не попадает.",
        };
        var connections = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        connections.Children.Add(bar);
        connections.Children.Add(note);
        connections.Children.Add(_grid);

        // ---- Hosts tab ----
        _hosts = CreateGrid();
        _hosts.Columns.Add(Col("Хост", nameof(HostSummary.Host), 220));
        _hosts.Columns.Add(Col("Запросов", nameof(HostSummary.Requests), 70));
        _hosts.Columns.Add(Col("Ошибок", nameof(HostSummary.Failures), 60));
        _hosts.Columns.Add(Col("IP-адреса", nameof(HostSummary.RemoteIps), 220));
        _hosts.Columns.Add(Col("Протоколы", nameof(HostSummary.Protocols), 90));
        _hosts.Columns.Add(Col("TLS", nameof(HostSummary.Tls), 80));
        _hosts.Columns.Add(Col("Издатель сертификата", nameof(HostSummary.Issuer), 260));
        _hosts.Columns.Add(Col("Байт", nameof(HostSummary.Bytes), 90));
        _hostsTab = new TabItem { Header = "Хосты", Content = _hosts };

        // ---- Probe tab ----
        var probeBar = new WrapPanel { Margin = new Thickness(8) };
        probeBar.Children.Add(Btn("Проверить заново", (_, _) => RerunProbe()));
        probeBar.Children.Add(Btn("Сохранить отчёт…", (_, _) => SaveReport()));
        probeBar.Children.Add(_probeStatus);
        DockPanel.SetDock(probeBar, Dock.Top);
        _probePanel.Children.Add(probeBar);
        _probeTab = new TabItem { Header = "IP и отпечаток", Content = _probePanel };

        _tabs.Items.Add(new TabItem { Header = "Соединения", Content = connections });
        _tabs.Items.Add(_hostsTab);
        _tabs.Items.Add(_probeTab);
        _tabs.SelectionChanged += OnTabChanged;
        Content = _tabs;

        _session.Connections.Added += OnAdded;
        _session.Connections.Cleared += OnCleared;
        _hostTimer.Tick += (_, _) => RefreshHosts();
        Closed += (_, _) =>
        {
            _session.Connections.Added -= OnAdded;
            _session.Connections.Cleared -= OnCleared;
            _hostTimer.Stop();
            _probe?.Dispose();
            _probe = null;
        };
        UpdateCounter();
    }

    /// <summary>Opens the window on a given tab: 0 connections, 1 hosts, 2 IP and fingerprint.</summary>
    public void SelectTab(int index) => _tabs.SelectedIndex = index;

    private static DataGrid CreateGrid() => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        EnableRowVirtualization = true,
        EnableColumnVirtualization = true,
        SelectionMode = DataGridSelectionMode.Extended,
        ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
        FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI"),
        FontSize = 12,
    };

    private static DataGridTextColumn Col(string header, string path, double width) =>
        new() { Header = header, Binding = new Binding(path), Width = new DataGridLength(width) };

    private static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        b.Click += click;
        return b;
    }

    private bool Matches(object o)
    {
        if (o is not ConnectionEntry e) return false;
        if (_errorsOnly.IsChecked == true && e.Outcome is not (ConnectionOutcome.Failed or ConnectionOutcome.Blocked) && e.Status is not >= 400) return false;
        var q = _filter.Text.Trim();
        if (q.Length == 0) return true;
        return Contains(e.Url, q) || Contains(e.RemoteEndpoint, q) || Contains(e.StatusText, q) || Contains(e.Error, q)
               || Contains(e.ResourceType, q) || Contains(e.Protocol, q) || Contains(e.Source, q) || Contains(e.CertificateIssuer, q);
    }

    private static bool Contains(string? s, string q) => s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void RefreshView()
    {
        _view.Refresh();
        UpdateCounter();
    }

    private void OnAdded(ConnectionEntry e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => OnAdded(e)); return; }
        _rows.Add(e);
        while (_rows.Count > _session.Connections.Capacity) _rows.RemoveAt(0);
        if (_autoScroll.IsChecked == true && Matches(e)) _grid.ScrollIntoView(e);
        UpdateCounter();
    }

    private void OnCleared()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(OnCleared); return; }
        _rows.Clear();
        RefreshHosts();
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        var failed = _rows.Count(r => r.Outcome is ConnectionOutcome.Failed or ConnectionOutcome.Blocked);
        var hosts = _rows.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var file = _session.LogFile is { } f ? $" · файл: {Path.GetFileName(f.FilePath)}" : " · запись в файл недоступна";
        _counter.Text = $"Запросов: {_rows.Count}, ошибок: {failed}, хостов: {hosts}{file}";
    }

    private void RefreshHosts() => _hosts.ItemsSource = ConnectionLog.Summarize(_rows);

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _tabs)) return;
        if (_tabs.SelectedItem == _hostsTab) { RefreshHosts(); _hostTimer.Start(); }
        else _hostTimer.Stop();
        if (_tabs.SelectedItem == _probeTab && !_probeStarted) StartProbe();
    }

    private async void StartProbe()
    {
        _probeStarted = true;
        _probeStatus.Text = "Запуск проверки внутри профиля…";
        _probe = new WebView2();
        _probePanel.Children.Add(_probe);
        var error = await _engine.InitializeProbeViewAsync(_session, _probe, OnReport);
        if (error is not null) _probeStatus.Text = error;
        else _probeStatus.Text = "Сбор данных: внешний IP, WebRTC, заголовки, отпечаток браузера…";
    }

    private void RerunProbe()
    {
        if (_probe?.CoreWebView2 is null) { if (!_probeStarted) StartProbe(); return; }
        _lastReport = null;
        _probeStatus.Text = "Повторная проверка…";
        _probe.CoreWebView2.Navigate(WebView2Engine.ProbeUri);
    }

    private void OnReport(string json)
    {
        _lastReport = json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err)) { _probeStatus.Text = "Проверка завершилась с ошибкой: " + err.GetString(); return; }
            var ip = root.TryGetProperty("sections", out var s) && s.TryGetProperty("Сеть", out var net) && net.TryGetProperty("IP (ipinfo.io)", out var v) ? v.ToString() : "?";
            var id = root.TryGetProperty("fingerprintId", out var f) ? f.GetString() : "?";
            _probeStatus.Text = $"Готово: IP {ip}, ID отпечатка {id}. {DateTime.Now:HH:mm:ss}";
        }
        catch (JsonException)
        {
            _probeStatus.Text = "Получен некорректный отчёт.";
        }
    }

    private void SaveLog()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Таблица TSV (*.tsv)|*.tsv|JSON (*.json)|*.json",
            FileName = $"connections-{DateTime.Now:yyyyMMdd-HHmmss}.tsv",
            Title = "Сохранить журнал соединений",
        };
        if (dialog.ShowDialog(this) != true) return;
        var text = dialog.FilterIndex == 2 ? _session.Connections.ExportJson() : _session.Connections.ExportTsv();
        File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true)); // BOM so Excel opens Cyrillic correctly
    }

    private void SaveReport()
    {
        if (_lastReport is null)
        {
            ChoiceDialog.Show(this, "Отчёт", "Проверка ещё не завершена. Откройте вкладку «IP и отпечаток» и дождитесь результата.", ["ОК"], 0, 0);
            return;
        }
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"fingerprint-{DateTime.Now:yyyyMMdd-HHmmss}.json", Title = "Сохранить отчёт" };
        if (dialog.ShowDialog(this) != true) return;
        string pretty;
        try { pretty = JsonSerializer.Serialize(JsonDocument.Parse(_lastReport).RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
        catch (JsonException) { pretty = _lastReport; }
        File.WriteAllText(dialog.FileName, pretty, new UTF8Encoding(false));
    }

    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logDirectory}\"") { UseShellExecute = false });
    }
}
