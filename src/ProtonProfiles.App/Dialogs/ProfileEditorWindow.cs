using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.App.Dialogs;

/// <summary>Per-profile settings editor (spec §5, F06). Settings marked * require a profile restart.</summary>
public sealed class ProfileEditorWindow : Window
{
    private readonly ProfileConfig _original;
    private readonly BrowserCapabilities _capabilities;
    private readonly TextBox _name = new();
    private readonly TextBox _testUrl = new();
    private readonly ComboBox _graphics = new();
    private readonly TextBox _label = new();
    private readonly TextBox _color = new();
    private readonly ComboBox _network = new();
    private readonly TextBox _proxyAddress = new();
    private readonly ComboBox _proxyAuth = new();
    private readonly TextBox _proxyUser = new();
    private readonly PasswordBox _proxyPassword = new();
    private readonly ComboBox _uaMode = new();
    private readonly TextBox _uaValue = new();
    private readonly ComboBox _langMode = new();
    private readonly TextBox _langTag = new();
    private readonly ComboBox _slMode = new();
    private readonly TextBox _slTag = new();
    private readonly TextBox _timeZone = new();
    private readonly ComboBox _scheme = new();
    private readonly TextBox _zoom = new();
    private readonly ComboBox _tracking = new();
    private readonly ComboBox _webRtcPage = new();
    private readonly ComboBox _webRtcNetwork = new();
    private readonly TextBox _downloads = new() { IsReadOnly = true };
    private readonly TextBox _reminder = new();
    private readonly TextBlock _errors = new() { Foreground = System.Windows.Media.Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };

    public ProfileConfig? Result { get; private set; }
    /// <summary>A newly entered proxy secret; written to Credential Manager by the caller, never stored in the config.</summary>
    public ProxyCredential? NewCredential { get; private set; }

    public ProfileEditorWindow(Window owner, ProfileConfig profile, BrowserCapabilities capabilities)
    {
        _original = profile;
        _capabilities = capabilities;
        Owner = owner;
        Title = $"Настройки профиля «{profile.DisplayName}»";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        void Add(string label, UIElement control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(l); grid.Children.Add(control);
            row++;
        }

        _network.ItemsSource = new[] { "Системная сеть Windows", capabilities.ProxySupport == ProxySupportLevel.None ? "Прокси (недоступно в этой сборке)" : "Прокси HTTP (экспериментально)", "Не выбрано" };
        _proxyAuth.ItemsSource = new[] { "Без аутентификации", "Basic (логин и пароль)" };
        _uaMode.ItemsSource = new[] { "По умолчанию (текущая среда выполнения)", "Свой" };
        _langMode.ItemsSource = new[] { "Системный", "Свой тег BCP 47" };
        _slMode.ItemsSource = new[] { "По умолчанию", "Как язык браузера", "Свой тег BCP 47" };
        _scheme.ItemsSource = new[] { "Системная", "Светлая", "Тёмная" };
        _tracking.ItemsSource = new[] { "Сбалансированная", "Строгая (проверьте совместимость)" };
        _webRtcPage.ItemsSource = new[] { "Блокировать доступ страниц (по умолчанию)", "Разрешить для совместимости" };
        _webRtcNetwork.ItemsSource = new[] { "Настройки среды выполнения", capabilities.WebRtcNetworkRestrictionSupported
            ? "Ограничить UDP вне прокси (экспериментально)" : "Ограничение UDP (недоступно в этой сборке)" };

        _graphics.ItemsSource = new[] { "Настройки среды выполнения", "WebGL/WebGPU (экспериментально)", "WebGL/WebGPU + Canvas (экспериментально)", "WebGL/WebGPU + Canvas + Web Audio (экспериментально)" };
        Add("Тип профиля", new TextBlock { Text = profile.Kind == ProfileKind.Test ? "Тестовый — произвольные HTTP/HTTPS сайты" : "Почтовый — Proton Mail", TextWrapping = TextWrapping.Wrap });
        if (profile.Kind == ProfileKind.Test) Add("Начальный URL *", _testUrl);
        Add("Название", _name);
        Add("Метка адреса (необязательно)", _label);
        Add("Цвет (#RRGGBB)", _color);
        Add("Сеть *", _network);
        Add("Адрес прокси (http://узел:порт) *", _proxyAddress);
        Add("Аутентификация прокси *", _proxyAuth);
        Add("Логин прокси *", _proxyUser);
        Add("Пароль прокси * (пусто — не менять)", _proxyPassword);
        Add("User-Agent *", _uaMode);
        Add("Строка User-Agent *", _uaValue);
        Add("Язык браузера *", _langMode);
        Add("Тег языка *", _langTag);
        Add("Локаль JavaScript (Intl) *", _slMode);
        Add("Тег локали *", _slTag);
        _timeZone.ToolTip = "Пусто — системный. Например: Europe/Berlin, America/New_York, UTC. Изменение требует перезапуска.";
        Add("Часовой пояс браузера (IANA) *", _timeZone);
        Add("Тема", _scheme);
        Add("Масштаб (0,5–2,0)", _zoom);
        Add("Защита от отслеживания *", _tracking);
        Add("Защита отпечатка *", _graphics);
        Add("Влияние на сайты", new TextBlock { Text = "WebGL/WebGPU отключаются; 3D и карты могут не работать, видео замедлиться. Canvas означает запрет чтения/экспорта пикселей; рисование сохраняется. Web Audio блокирует AudioContext и OfflineAudioContext в документах: аудиоэффекты, игры и визуализаторы могут не работать. CPU, память, экран и шрифты остаются доступными.", TextWrapping = TextWrapping.Wrap });
        Add("Доступ страниц к WebRTC *", _webRtcPage);
        Add("Сеть WebRTC *", _webRtcNetwork);
        Add("Границы защиты", new TextBlock { Text = "Блокировка страниц не отключает WebRTC в браузере. Ограничение сети экспериментальное. Отсутствие утечек не подтверждено; полная проверка требует Windows и контролируемого стенда.", TextWrapping = TextWrapping.Wrap });
        var dl = new DockPanel();
        var choose = new Button { Content = "Выбрать…", Margin = new Thickness(6, 0, 0, 0) };
        choose.Click += (_, _) => ChooseDownloads();
        DockPanel.SetDock(choose, Dock.Right);
        dl.Children.Add(choose);
        dl.Children.Add(_downloads);
        Add("Папка для вложений", dl);
        if (profile.Kind == ProfileKind.Mail) Add("Напоминать через (месяцев, 1–12)", _reminder);

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var note = new TextBlock
        {
            Text = "* — изменение применяется после перезапуска профиля. Часовой пояс применяется к окнам профиля; покрытие workers и отдельных процессов фреймов требует проверки. Canvas 2D, Audio и шрифты не изменяются: разные настройки не делают аккаунты несвязываемыми.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(note, row); Grid.SetColumnSpan(note, 2); grid.Children.Add(note); row++;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_errors, row); Grid.SetColumnSpan(_errors, 2); grid.Children.Add(_errors); row++;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Сохранить", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, row); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);

        Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Load(profile);
        _network.SelectionChanged += (_, _) => UpdateEnabled();
        _proxyAuth.SelectionChanged += (_, _) => UpdateEnabled();
        _uaMode.SelectionChanged += (_, _) => UpdateEnabled();
        _langMode.SelectionChanged += (_, _) => UpdateEnabled();
        _slMode.SelectionChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();
    }

    private void Load(ProfileConfig p)
    {
        _testUrl.Text = p.TestStartUrl ?? string.Empty;
        _graphics.SelectedIndex = (int)p.GraphicsPolicy;
        _name.Text = p.DisplayName;
        _label.Text = p.EmailLabel ?? string.Empty;
        _color.Text = p.Color;
        _network.SelectedIndex = p.NetworkMode switch { NetworkMode.System => 0, NetworkMode.Proxy => 1, _ => 2 };
        _proxyAddress.Text = p.Proxy?.Endpoint?.ToString() ?? string.Empty;
        _proxyAuth.SelectedIndex = p.Proxy?.AuthMode == ProxyAuthMode.Basic ? 1 : 0;
        _uaMode.SelectedIndex = (int)p.UserAgentMode;
        _uaValue.Text = p.CustomUserAgent ?? string.Empty;
        _langMode.SelectedIndex = (int)p.LanguageMode;
        _langTag.Text = p.LanguageTag ?? string.Empty;
        _slMode.SelectedIndex = (int)p.ScriptLocaleMode;
        _slTag.Text = p.ScriptLocaleTag ?? string.Empty;
        _timeZone.Text = p.BrowserTimeZoneId ?? string.Empty;
        _scheme.SelectedIndex = (int)p.ColorScheme;
        _zoom.Text = p.ZoomFactor.ToString("0.##", CultureInfo.CurrentCulture);
        _tracking.SelectedIndex = (int)p.TrackingPreventionLevel;
        _webRtcPage.SelectedIndex = (int)p.WebRtcPagePolicy;
        _webRtcNetwork.SelectedIndex = (int)p.WebRtcNetworkPolicy;
        _downloads.Text = p.DownloadDirectory ?? string.Empty;
        _reminder.Text = p.ReminderMonths.ToString(CultureInfo.CurrentCulture);
    }

    private void UpdateEnabled()
    {
        var proxy = _network.SelectedIndex == 1;
        _proxyAddress.IsEnabled = proxy;
        _proxyAuth.IsEnabled = proxy;
        _proxyUser.IsEnabled = _proxyPassword.IsEnabled = proxy && _proxyAuth.SelectedIndex == 1;
        _uaValue.IsEnabled = _uaMode.SelectedIndex == 1;
        _langTag.IsEnabled = _langMode.SelectedIndex == 1;
        _slTag.IsEnabled = _slMode.SelectedIndex == 2;
    }

    private void ChooseDownloads()
    {
        var dialog = new OpenFolderDialog { Title = "Папка для сохранения вложений" };
        if (!string.IsNullOrEmpty(_downloads.Text)) dialog.InitialDirectory = _downloads.Text;
        if (dialog.ShowDialog(this) == true) _downloads.Text = dialog.FolderName;
    }

    private void Save()
    {
        var errors = new List<string>();
        var networkMode = _network.SelectedIndex switch { 0 => NetworkMode.System, 1 => NetworkMode.Proxy, _ => NetworkMode.Unset };
        ProxySettings? proxy = null;
        NewCredential = null;
        if (networkMode == NetworkMode.Proxy)
        {
            ProxyEndpoint? endpoint = null;
            if (!string.IsNullOrWhiteSpace(_proxyAddress.Text) && !ProxyEndpoint.TryParse(_proxyAddress.Text, out endpoint, out var epError))
                errors.Add(epError!);
            var auth = _proxyAuth.SelectedIndex == 1 ? ProxyAuthMode.Basic : ProxyAuthMode.None;
            var credRef = auth == ProxyAuthMode.Basic ? _original.Proxy?.CredentialRef : null;
            if (auth == ProxyAuthMode.Basic && _proxyPassword.Password.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(_proxyUser.Text)) errors.Add("Укажите логин прокси.");
                else NewCredential = new ProxyCredential(_proxyUser.Text.Trim(), _proxyPassword.Password);
                credRef = null; // replaced by the caller with a fresh reference
            }
            proxy = new ProxySettings(endpoint, auth, credRef);
        }

        if (!double.TryParse(_zoom.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)) zoom = double.NaN;
        if (!int.TryParse(_reminder.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var months)) months = 0;

        var edited = _original with
        {
            DisplayName = _name.Text.Trim(),
            TestStartUrl = _original.Kind == ProfileKind.Test ? _testUrl.Text.Trim() : null,
            GraphicsPolicy = (GraphicsPolicy)_graphics.SelectedIndex,
            EmailLabel = string.IsNullOrWhiteSpace(_label.Text) ? null : _label.Text.Trim(),
            Color = _color.Text.Trim(),
            NetworkMode = networkMode,
            Proxy = proxy,
            UserAgentMode = (UserAgentMode)_uaMode.SelectedIndex,
            CustomUserAgent = _uaMode.SelectedIndex == 1 ? _uaValue.Text : null,
            LanguageMode = (LanguageMode)_langMode.SelectedIndex,
            LanguageTag = _langMode.SelectedIndex == 1 ? _langTag.Text.Trim() : null,
            ScriptLocaleMode = (ScriptLocaleMode)_slMode.SelectedIndex,
            ScriptLocaleTag = _slMode.SelectedIndex == 2 ? _slTag.Text.Trim() : null,
            BrowserTimeZoneId = string.IsNullOrWhiteSpace(_timeZone.Text) ? null : _timeZone.Text.Trim(),
            ColorScheme = (ColorSchemePreference)_scheme.SelectedIndex,
            ZoomFactor = zoom,
            TrackingPreventionLevel = (TrackingPreventionLevel)_tracking.SelectedIndex,
            WebRtcPagePolicy = (WebRtcPagePolicy)_webRtcPage.SelectedIndex,
            WebRtcNetworkPolicy = (WebRtcNetworkPolicy)_webRtcNetwork.SelectedIndex,
            DownloadDirectory = string.IsNullOrWhiteSpace(_downloads.Text) ? null : _downloads.Text,
            ReminderMonths = months,
        };
        errors.AddRange(ProfileValidator.Validate(edited));
        if (errors.Count > 0)
        {
            _errors.Text = string.Join("\n", errors.Distinct());
            return;
        }
        Result = edited;
        DialogResult = true;
    }
}
