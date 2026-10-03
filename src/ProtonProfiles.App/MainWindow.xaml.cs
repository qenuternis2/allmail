using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using ProtonProfiles.App.Browser;
using ProtonProfiles.App.Dialogs;
using ProtonProfiles.App.ViewModels;
using ProtonProfiles.Core;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Downloads;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Reminders;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.App;

public partial class MainWindow : Window, IBrowserViewHost
{
    private readonly ManagedPaths _paths;
    private readonly IProfileRepository _repository;
    private readonly ProfileCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly PermissionPolicy _permissions;
    private readonly string _runtimeVersion;
    private readonly ObservableCollection<ProfileItem> _items = [];
    private readonly Dictionary<Guid, WebView2> _views = [];
    private readonly Dictionary<Guid, int> _activeDownloads = [];
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private ProfileLifecycleService _lifecycle = null!;
    private WebView2Engine? _engine;
    private readonly Dictionary<Guid, ProfileDiagnosticsWindow> _diagnosticsWindows = [];
    private (GenerationContext Context, string Uri)? _pendingExternal;
    private bool _shutdownConfirmed;

    public MainWindow(ManagedPaths paths, IProfileRepository repository, ProfileCatalog catalog, ICredentialStore credentials, PermissionPolicy permissions, string runtimeVersion)
    {
        InitializeComponent();
        _paths = paths;
        _repository = repository;
        _catalog = catalog;
        _credentials = credentials;
        _permissions = permissions;
        _runtimeVersion = runtimeVersion;
        ProfileList.ItemsSource = _items;
        _reminderTimer.Tick += (_, _) => RefreshReminders();
    }

    public void Initialize(IBrowserEngine engine)
    {
        _lifecycle = new ProfileLifecycleService(_repository, engine, _credentials, _paths);
        _engine = engine as WebView2Engine;
        _lifecycle.StateChanged += state => Dispatcher.InvokeAsync(() => OnStateChanged(state));
        ExperimentalBanner.Visibility = engine.Capabilities.IsExperimentalNetworking ? Visibility.Visible : Visibility.Collapsed;
        Title = engine.Capabilities.IsExperimentalNetworking ? "Proton Profiles — экспериментальная сборка с прокси" : "Proton Profiles";
        Reload();
        _reminderTimer.Start();
        StatusBarText.Text = $"WebView2 Runtime {_runtimeVersion} · SDK {WebView2Engine.SdkVersion}";
    }

    public async Task ResumeInterruptedOperationsAsync()
    {
        var results = await _lifecycle.ResumePendingOperationsAsync();
        var incomplete = results.Where(r => !r.Report.Completed).ToList();
        Reload();
        if (incomplete.Count > 0)
            ChoiceDialog.Show(this, "Незавершённая очистка",
                "Некоторые операции сброса или удаления не завершены (файлы заблокированы или доступ запрещён). Они будут повторены при следующем запуске.\n\n" +
                string.Join("\n", incomplete.Select(r => "• " + r.Report.Message)), ["ОК"], 0, 0);
        var due = _items.Where(i => i.ReminderDue).ToList();
        if (due.Count > 0)
            StatusBarText.Text = $"Пора проверить ящики: {string.Join(", ", due.Select(d => d.DisplayName))}";
    }

    // ---------------- List ----------------

    private ProfileItem? Selected => ProfileList.SelectedItem as ProfileItem;

    private void Reload()
    {
        var selectedId = Selected?.Id;
        var query = SearchBox.Text;
        _items.Clear();
        foreach (var p in ProfileCatalog.Filter(_catalog.List(), query))
        {
            var item = new ProfileItem(p, _lifecycle.GetState(p.Id));
            Refresh(item);
            _items.Add(item);
        }
        if (selectedId is not null) ProfileList.SelectedItem = _items.FirstOrDefault(i => i.Id == selectedId);
        UpdateSelectedPanel();
    }

    private void Refresh(ProfileItem item)
    {
        item.Reminder = ReminderCalculator.Evaluate(item.Config, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        item.Readiness = NetworkReadinessEvaluator.Evaluate(item.Config, _lifecycle.Capabilities, _credentials);
    }

    private void RefreshReminders()
    {
        foreach (var i in _items) Refresh(i);
        UpdateSelectedPanel();
    }

    private void ReloadItem(Guid id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        var config = _repository.Get(id);
        if (item is null || config is null) { Reload(); return; }
        item.Config = config;
        item.State = _lifecycle.GetState(id);
        Refresh(item);
        UpdateSelectedPanel();
    }

    private void OnStateChanged(ProfileRuntimeState state)
    {
        var item = _items.FirstOrDefault(i => i.Id == state.ProfileId);
        if (item is not null)
        {
            item.State = state;
            var config = _repository.Get(state.ProfileId);
            if (config is not null) item.Config = config;
            Refresh(item);
        }
        UpdateSelectedPanel();
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Reload();

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Selected is { } s && _lifecycle.GetState(s.Id).Phase == LifecyclePhase.Open) _lifecycle.MarkActivated(s.Id);
        UpdateSelectedPanel();
    }

    private async void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Selected is { } s && _lifecycle.GetState(s.Id).Phase == LifecyclePhase.Closed) await OpenProfileAsync(s.Id);
    }

    private void UpdateSelectedPanel()
    {
        var s = Selected;
        foreach (var (id, view) in _views) view.Visibility = s is not null && id == s.Id ? Visibility.Visible : Visibility.Hidden;
        if (s is null)
        {
            SelectedName.Text = "Профиль не выбран";
            SelectedStatus.Text = string.Empty;
            Placeholder.Visibility = Visibility.Visible;
            return;
        }
        // The top bar always shows the selected local profile name (spec §3).
        SelectedName.Text = s.DisplayName;
        SelectedStatus.Text = $"{s.StatusText} · {s.ReminderText}";
        var phase = s.State.Phase;
        OpenButton.Content = phase is LifecyclePhase.Open or LifecyclePhase.Starting ? "Закрыть" : "Открыть";
        OpenButton.IsEnabled = phase is not LifecyclePhase.Closing and not LifecyclePhase.RecoveryRequired;
        RecoveryButton.Visibility = phase == LifecyclePhase.RecoveryRequired ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = _views.ContainsKey(s.Id) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- Lifecycle commands ----------------

    private async void OnOpenClose(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var phase = _lifecycle.GetState(s.Id).Phase;
        if (phase is LifecyclePhase.Open or LifecyclePhase.Starting) await CloseProfileAsync(s.Id, confirm: true);
        else await OpenProfileAsync(s.Id);
    }

    private async Task OpenProfileAsync(Guid id)
    {
        while (true)
        {
            var result = await _lifecycle.OpenAsync(id);
            switch (result.Outcome)
            {
                case OpenOutcome.Opened:
                case OpenOutcome.AlreadyOpen:
                    ReloadItem(id);
                    return;
                case OpenOutcome.CapacityReached:
                    // Never evict silently: the user picks a profile to close or cancels (spec §4.4, A30).
                    var live = result.Capacity!.AllLive.ToList();
                    var names = live.Select(l => $"Закрыть «{_repository.Get(l.ProfileId)?.DisplayName ?? "?"}»" + (l.IsPinned ? " (закреплён)" : string.Empty)).ToList();
                    names.Add("Отмена");
                    var suggested = result.Capacity.SuggestedToClose.FirstOrDefault();
                    var pick = ChoiceDialog.Show(this, "Слишком много открытых профилей",
                        $"Одновременно могут быть открыты не более {CapacityPolicy.DefaultMaxLiveEnvironments} профилей. Выберите профиль, который нужно закрыть, или отмените открытие.\n\n" +
                        "Перед закрытием сохраните черновики: приложение не может надёжно определить, сохранены ли они.",
                        names, suggested is null ? names.Count - 1 : live.FindIndex(l => l.ProfileId == suggested.ProfileId), names.Count - 1);
                    if (pick is null || pick == names.Count - 1) return;
                    var closed = await CloseProfileAsync(live[pick.Value].ProfileId, confirm: true);
                    if (!closed) return;
                    continue;
                case OpenOutcome.Blocked when result.Readiness == NetworkReadiness.CredentialsRequired:
                    if (!PromptProxyCredential(id)) return;
                    continue;
                default:
                    ReloadItem(id);
                    if (result.Outcome != OpenOutcome.Cancelled)
                        ChoiceDialog.Show(this, "Профиль не открыт", result.Message ?? result.Outcome.ToString(), ["ОК"], 0, 0);
                    return;
            }
        }
    }

    private async Task<bool> CloseProfileAsync(Guid id, bool confirm)
    {
        var name = _repository.Get(id)?.DisplayName ?? "?";
        if (confirm && !ConfirmClose(id, name)) return false;
        var result = await _lifecycle.CloseAsync(id);
        ReloadItem(id);
        if (result.Outcome == CloseOutcome.RecoveryRequired)
        {
            ChoiceDialog.Show(this, "Требуется восстановление", $"«{name}»: {result.Message}\nДанные профиля не удалялись. Нажмите «Повторить восстановление» позже.", ["ОК"], 0, 0);
            return false;
        }
        return true;
    }

    /// <summary>Warns about unsaved edits and active downloads regardless of draft detection (spec §4.4).</summary>
    private bool ConfirmClose(Guid id, string name)
    {
        if (_lifecycle.GetState(id).Phase is not LifecyclePhase.Open) return true;
        var downloads = _activeDownloads.GetValueOrDefault(id);
        var text = $"Закрыть профиль «{name}»?\n\nНесохранённые черновики и изменения на странице могут быть потеряны.";
        if (downloads > 0) text += $"\nАктивные загрузки ({downloads}) будут прерваны.";
        return ChoiceDialog.Show(this, "Закрытие профиля", text, ["Закрыть", "Отмена"], 1, 1) == 0;
    }

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        if (!ConfirmClose(s.Id, s.DisplayName)) return;
        var r = await _lifecycle.RestartAsync(s.Id);
        ReloadItem(s.Id);
        if (r.Outcome is not (OpenOutcome.Opened or OpenOutcome.AlreadyOpen))
            OfferRevertIfPending(s.Id, r.Message ?? r.Outcome.ToString());
    }

    private void OfferRevertIfPending(Guid id, string message)
    {
        var p = _repository.Get(id);
        if (p?.PendingRevision is null || p.LastAppliedRevision is null)
        {
            ChoiceDialog.Show(this, "Перезапуск не выполнен", message, ["ОК"], 0, 0);
            return;
        }
        // No silent fallback to the old route or to System (spec §6.3 step 6).
        var pick = ChoiceDialog.Show(this, "Новые настройки не применены",
            message + "\n\nПрофиль остаётся закрытым. Можно повторить попытку или явно вернуть последние применённые настройки.",
            ["Повторить", "Вернуть прежние настройки", "Оставить закрытым"], 0, 2);
        if (pick == 1) { _catalog.RevertToLastApplied(id); ReloadItem(id); }
        if (pick == 0) OnRestart(this, new RoutedEventArgs());
    }

    private async void OnRetryRecovery(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var r = await _lifecycle.RetryRecoveryAsync(s.Id);
        ReloadItem(s.Id);
        if (r.Outcome == CloseOutcome.RecoveryRequired)
            ChoiceDialog.Show(this, "Восстановление", r.Message ?? "Процесс браузера всё ещё работает.", ["ОК"], 0, 0);
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var pick = ChoiceDialog.Show(this, "Сброс локальной сессии",
            $"Сбросить локальную сессию профиля «{s.DisplayName}»?\n\n" +
            "Будут удалены данные сайтов этого профиля в приложении (вход, кэш, локальное хранилище) и сохранённые решения о разрешениях. " +
            "Несинхронизированная работа может быть потеряна. Настройки профиля и сохранённые вложения останутся.\n\n" +
            "Это не выход из Proton на сервере и не удаление аккаунта Proton.",
            [$"Сбросить «{s.DisplayName}»", "Отмена"], 1, 1);
        if (pick != 0) return;
        var report = await _lifecycle.ResetLocalSessionAsync(s.Id);
        ReloadItem(s.Id);
        ChoiceDialog.Show(this, "Сброс сессии", report.Completed ? "Локальная сессия сброшена. При следующем открытии потребуется вход." : "Сброс не завершён: " + report.Message, ["ОК"], 0, 0);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var pick = ChoiceDialog.Show(this, "Удаление локального профиля",
            $"Удалить локальный профиль «{s.DisplayName}»?\n\n" +
            "Будут удалены его настройки, данные браузера в приложении и учётные данные прокси. Вложения, сохранённые в выбранные вами папки, не удаляются.\n\n" +
            "Аккаунт Proton не удаляется, и его сеансы на сервере не отзываются.",
            [$"Удалить «{s.DisplayName}»", "Отмена"], 1, 1);
        if (pick != 0) return;
        var report = await _lifecycle.DeleteLocalProfileAsync(s.Id);
        Reload();
        if (!report.Completed)
            ChoiceDialog.Show(this, "Удаление не завершено", (report.Message ?? string.Empty) + "\nОчистка будет продолжена при следующем запуске.", ["ОК"], 0, 0);
    }

    // ---------------- Metadata commands ----------------

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = ChoiceDialog.Prompt(this, "Новый профиль", "Название профиля (1–100 символов):");
        if (name is null) return;
        var label = ChoiceDialog.Prompt(this, "Новый профиль", "Метка адреса (необязательно; это только подпись, а не проверенный адрес):");
        var palette = new[] { "#2563EB", "#059669", "#D97706", "#DC2626", "#7C3AED", "#0891B2", "#DB2777", "#4B5563" };
        var color = palette[_catalog.List().Count % palette.Length];
        var result = _catalog.Create(name, label, color, out var created);
        if (!result.Saved)
        {
            ChoiceDialog.Show(this, "Профиль не создан", string.Join("\n", result.Errors), ["ОК"], 0, 0);
            return;
        }
        Reload();
        ProfileList.SelectedItem = _items.FirstOrDefault(i => i.Id == created!.Id);
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var current = _repository.Get(s.Id)!;
        var editor = new ProfileEditorWindow(this, current, _lifecycle.Capabilities);
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var edited = editor.Result;
        if (editor.NewCredential is not null)
        {
            // Fresh reference per secret: older generations can never read the new value (spec §6.1, A24).
            var reference = _credentials.Write(s.Id, editor.NewCredential);
            edited = edited with { Proxy = edited.Proxy! with { CredentialRef = reference } };
        }
        var live = _lifecycle.GetState(s.Id).Phase != LifecyclePhase.Closed;
        var save = _catalog.SaveSettings(edited, live);
        if (!save.Saved)
        {
            ChoiceDialog.Show(this, "Настройки не сохранены", string.Join("\n", save.Errors), ["ОК"], 0, 0);
            return;
        }
        ReloadItem(s.Id);
        if (live && !save.RestartRequired) ApplyLiveSettings(s.Id, edited);
        if (save.RestartRequired)
        {
            var pick = ChoiceDialog.Show(this, "Требуется перезапуск",
                "Изменения сети, User-Agent, языка, локали или защиты от отслеживания вступят в силу после перезапуска профиля. Текущая страница будет закрыта.",
                ["Перезапустить сейчас", "Позже"], 0, 1);
            if (pick == 0 && ConfirmClose(s.Id, s.DisplayName))
            {
                var r = await _lifecycle.RestartAsync(s.Id);
                ReloadItem(s.Id);
                if (r.Outcome is not (OpenOutcome.Opened or OpenOutcome.AlreadyOpen)) OfferRevertIfPending(s.Id, r.Message ?? r.Outcome.ToString());
            }
        }
    }

    /// <summary>Theme and zoom apply live (spec §5).</summary>
    private void ApplyLiveSettings(Guid id, ProfileConfig config)
    {
        if (!_views.TryGetValue(id, out var view) || view.CoreWebView2 is null) return;
        try
        {
            view.CoreWebView2.Profile.PreferredColorScheme = WebView2Engine.ToApi(config.ColorScheme);
            view.ZoomFactor = config.ZoomFactor;
        }
        catch (COMException ex)
        {
            StatusBarText.Text = "Не удалось применить тему или масштаб: " + ex.Message;
        }
    }

    private bool PromptProxyCredential(Guid id)
    {
        var p = _repository.Get(id);
        if (p?.Proxy is null) return false;
        var user = ChoiceDialog.Prompt(this, "Учётные данные прокси", $"Профиль «{p.DisplayName}»: логин прокси");
        if (string.IsNullOrWhiteSpace(user)) return false;
        var password = ChoiceDialog.Prompt(this, "Учётные данные прокси", "Пароль прокси (хранится в диспетчере учётных данных Windows)", password: true);
        if (password is null) return false;
        var reference = _credentials.Write(id, new ProxyCredential(user.Trim(), password));
        var save = _catalog.SaveSettings(p with { Proxy = p.Proxy with { CredentialRef = reference } }, profileIsLive: false);
        ReloadItem(id);
        return save.Saved;
    }

    private void OnConfirmVisit(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        _catalog.ConfirmVisit(s.Id, TimeZoneInfo.Local);
        ReloadItem(s.Id);
    }

    private void OnReminderMenu(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var history = _repository.ListVisitConfirmations(s.Id);
        var text = new StringBuilder();
        text.AppendLine("Отметки — это локальные записи о ваших проверках, а не данные Proton об активности.");
        text.AppendLine($"Proton рекомендует входить в бесплатный аккаунт хотя бы раз в год; неактивные более 12 месяцев аккаунты могут быть удалены (сведения на {ReminderCalculator.PolicyReviewedOn}).");
        text.AppendLine();
        text.AppendLine(history.Count == 0 ? "Дата проверки не записана." : "Последние отметки:");
        foreach (var h in history.Take(5)) text.AppendLine($"• {h.ConfirmedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
        var buttons = new List<string> { "Отложить на неделю", "Политика Proton…" };
        if (history.Count > 0) buttons.Add("Отменить последнюю отметку");
        buttons.Add("Закрыть");
        var pick = ChoiceDialog.Show(this, $"Напоминания: «{s.DisplayName}»", text.ToString(), buttons, buttons.Count - 1, buttons.Count - 1);
        switch (pick)
        {
            case 0: _catalog.Snooze(s.Id, TimeSpan.FromDays(7)); break;
            case 1: Process.Start(new ProcessStartInfo(ReminderCalculator.PolicyHelpUrl) { UseShellExecute = true }); break;
            case 2 when history.Count > 0: _catalog.UndoVisitConfirmation(s.Id, history[0].Id); break;
        }
        ReloadItem(s.Id);
    }

    private void Move(int delta)
    {
        if (Selected is not { } s || !string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        var ids = _catalog.List().Select(p => p.Id).ToList();
        var i = ids.IndexOf(s.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        _catalog.Reorder(ids);
        Reload();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        _catalog.SetFavorite(s.Id, !s.Config.IsFavorite);
        ReloadItem(s.Id);
    }

    private void OnTogglePinned(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        _catalog.SetPinned(s.Id, !s.Config.IsPinned);
        ReloadItem(s.Id);
    }

    // ---------------- Import / export / diagnostics ----------------

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var includeProxy = ChoiceDialog.Show(this, "Экспорт настроек",
            "Экспортируются только названия, метки и настройки. Пароли, cookie, токены, папки браузера и учётные данные прокси не экспортируются.\n\nВключить адреса прокси? Они могут раскрыть вашу инфраструктуру.",
            ["Без адресов прокси", "С адресами прокси", "Отмена"], 0, 2);
        if (includeProxy is null or 2) return;
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "proton-profiles-settings.json" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, _catalog.Export(new ExportOptions(includeProxy == 1)), new UTF8Encoding(false));
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length > SettingsInterchange.MaxFileBytes)
        {
            ChoiceDialog.Show(this, "Импорт", "Файл больше 1 МиБ.", ["ОК"], 0, 0);
            return;
        }
        var result = _catalog.PreviewImport(File.ReadAllBytes(dialog.FileName));
        if (!result.Success)
        {
            ChoiceDialog.Show(this, "Импорт отклонён", "Ничего не импортировано.\n\n" + string.Join("\n", result.Errors.Take(20)), ["ОК"], 0, 0);
            return;
        }
        var preview = result.Preview!;
        var text = $"Будет создано профилей: {preview.Profiles.Count}.\n" +
                   string.Join("\n", preview.Profiles.Take(15).Select(p => "• " + p.DisplayName)) +
                   (preview.Notes.Count > 0 ? "\n\n" + string.Join("\n", preview.Notes.Take(15)) : string.Empty) +
                   "\n\nСессии, разрешения и учётные данные не переносятся. Профили не откроются автоматически.";
        if (ChoiceDialog.Show(this, "Предпросмотр импорта", text, ["Импортировать", "Отмена"], 0, 1) != 0) return;
        _catalog.CommitImport(preview);
        Reload();
    }

    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        var env = new EnvironmentInfo(
            AppVersion: typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "?",
            BuildFlavor: _lifecycle.Capabilities.IsExperimentalNetworking ? "ExperimentalProxy" : "Core",
            WebView2SdkVersion: WebView2Engine.SdkVersion,
            WebView2RuntimeVersion: _runtimeVersion,
            OsDescription: RuntimeInformation.OSDescription,
            DotNetVersion: RuntimeInformation.FrameworkDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString());
        var profiles = _catalog.List().Select(p => (p, _lifecycle.GetState(p.Id)));
        var json = DiagnosticsReport.Build(env, _lifecycle.Capabilities, profiles, []);
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "proton-profiles-diagnostics.json" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false));
    }

    private void OnConnectionLog(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        if (_diagnosticsWindows.TryGetValue(s.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        if (_engine is null || _lifecycle.GetSession(s.Id) is not WebView2Session session || session.IsClosing)
        {
            ChoiceDialog.Show(this, "Журнал и отпечаток", "Сначала откройте профиль: журнал соединений ведётся, пока профиль открыт, а проверка IP и отпечатка выполняется внутри него.", ["ОК"], 0, 0);
            return;
        }
        var window = new ProfileDiagnosticsWindow(this, s.DisplayName, session, _engine, _paths.ProfileLogDirectory(s.Id));
        session.RegisterAuxiliaryWindow(window);
        _diagnosticsWindows[s.Id] = window;
        window.Closed += (_, _) => _diagnosticsWindows.Remove(s.Id);
        window.Show();
    }

    // ---------------- External links ----------------

    private void OnOpenExternal(object sender, RoutedEventArgs e)
    {
        if (_pendingExternal is not { } p) return;
        ExternalLinkBar.Visibility = Visibility.Collapsed;
        _pendingExternal = null;
        // User-initiated, parsed http(s) only; no cookies or session data are transferred (spec §7, A29).
        if (!NavigationPolicy.IsExternalLaunchable(p.Uri)) return;
        Process.Start(new ProcessStartInfo(new Uri(p.Uri).AbsoluteUri) { UseShellExecute = true });
    }

    private void OnDismissExternal(object sender, RoutedEventArgs e)
    {
        ExternalLinkBar.Visibility = Visibility.Collapsed;
        _pendingExternal = null;
    }

    // ---------------- IBrowserViewHost ----------------

    void IBrowserViewHost.Attach(GenerationContext context, WebView2 view)
    {
        _views[context.ProfileId] = view;
        BrowserArea.Children.Add(view);
        UpdateSelectedPanel();
    }

    void IBrowserViewHost.Detach(GenerationContext context, WebView2 view)
    {
        BrowserArea.Children.Remove(view);
        if (_views.TryGetValue(context.ProfileId, out var current) && ReferenceEquals(current, view)) _views.Remove(context.ProfileId);
        _activeDownloads.Remove(context.ProfileId);
        UpdateSelectedPanel();
    }

    // Modal prompts run after the WebView2 event handler has returned (the deferral keeps the request open), so no
    // nested message pump runs inside a WebView2 callback (spec §4.6, S17).
    Task<UserPermissionAnswer?> IBrowserViewHost.AskPermissionAsync(GenerationContext context, string origin, PermissionKindKey kind) =>
        Dispatcher.InvokeAsync(() => AskPermission(context, origin, kind), DispatcherPriority.Background).Task;

    private UserPermissionAnswer? AskPermission(GenerationContext context, string origin, PermissionKindKey kind)
    {
        if (!_lifecycle.IsCurrentGeneration(context)) return null;
        var name = _repository.Get(context.ProfileId)?.DisplayName ?? "?";
        var pick = ChoiceDialog.Show(this, "Запрос разрешения",
            $"Профиль «{name}»: сайт {origin} запрашивает разрешение «{PermissionPolicy.DescribeKind(kind)}».\nРешение действует только для этого профиля.",
            ["Разрешить один раз", "Всегда разрешать", "Запретить", "Всегда запрещать"], 2, 2);
        return pick switch
        {
            0 => UserPermissionAnswer.AllowOnce,
            1 => UserPermissionAnswer.AlwaysAllow,
            2 => UserPermissionAnswer.DenyOnce,
            3 => UserPermissionAnswer.AlwaysDeny,
            _ => null,
        };
    }

    void IBrowserViewHost.OfferExternalLink(GenerationContext context, string uri)
    {
        if (!_lifecycle.IsCurrentGeneration(context)) return;
        _pendingExternal = (context, uri);
        ExternalLinkText.Text = "Ссылка за пределами Proton: " + Redactor.RedactUrl(uri);
        var proxy = _repository.Get(context.ProfileId)?.NetworkMode == NetworkMode.Proxy;
        ExternalLinkNote.Text = proxy
            ? "Внимание: внешний браузер не использует прокси этого профиля — у него собственная сессия и свои сетевые настройки."
            : "Внешний браузер использует собственную сессию и свои сетевые настройки.";
        ExternalLinkBar.Visibility = Visibility.Visible;
    }

    Task<string?> IBrowserViewHost.ChooseDownloadPathAsync(GenerationContext context, string sanitizedFileName, string? initialDirectory) =>
        Dispatcher.InvokeAsync(() => ChooseDownloadPath(context, sanitizedFileName, initialDirectory), DispatcherPriority.Background).Task;

    private string? ChooseDownloadPath(GenerationContext context, string sanitizedFileName, string? initialDirectory)
    {
        if (!_lifecycle.IsCurrentGeneration(context)) return null;
        var dir = initialDirectory is not null && Directory.Exists(initialDirectory)
            ? initialDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { } home ? Path.Combine(home, "Downloads") : null;
        var dialog = new SaveFileDialog { FileName = sanitizedFileName, InitialDirectory = dir, OverwritePrompt = true, Title = "Сохранить вложение" };
        if (dialog.ShowDialog(this) != true) return null;
        var target = dialog.FileName;
        var chosenDir = Path.GetDirectoryName(target)!;
        // Re-sanitize the user-edited name and keep the file inside the chosen folder.
        var final = Path.Combine(chosenDir, DownloadPaths.SanitizeFileName(Path.GetFileName(target)));
        return DownloadPaths.IsInside(chosenDir, final) ? final : null;
    }

    void IBrowserViewHost.ReportDownload(DownloadInfo info)
    {
        var id = info.Context.ProfileId;
        var count = _activeDownloads.GetValueOrDefault(id);
        _activeDownloads[id] = info.Phase == DownloadPhase.InProgress ? count + 1 : Math.Max(0, count - 1);
        StatusBarText.Text = info.Phase switch
        {
            DownloadPhase.InProgress => $"Загрузка: {info.FileName}…",
            DownloadPhase.Completed => $"Загрузка завершена: {info.FileName}",
            DownloadPhase.Interrupted => $"Загрузка прервана: {info.FileName} ({info.Reason})",
            DownloadPhase.Cancelled => $"Загрузка отменена: {info.FileName}",
            _ => StatusBarText.Text,
        };
    }

    void IBrowserViewHost.ReportProblem(GenerationContext context, string message)
    {
        if (!_lifecycle.IsCurrentGeneration(context)) return;
        StatusBarText.Text = $"«{_repository.Get(context.ProfileId)?.DisplayName}»: {message}";
    }

    async Task IBrowserViewHost.StopProfileAsync(GenerationContext context, string message)
    {
        if (!_lifecycle.IsCurrentGeneration(context)) return;
        var result = await _lifecycle.StopGenerationAsync(context, message);
        if (result.Outcome == CloseOutcome.AlreadyClosed) return;
        StatusBarText.Text = message + (result.Message is null ? string.Empty : " " + result.Message);
    }

    // ---------------- Shutdown ----------------

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownConfirmed) { base.OnClosing(e); return; }
        var live = _lifecycle.LiveProfiles();
        if (live.Count == 0) { base.OnClosing(e); return; }
        e.Cancel = true;
        var downloads = _activeDownloads.Values.Sum();
        var text = $"Открыто профилей: {live.Count}. Закрыть приложение?\nНесохранённые черновики могут быть потеряны." + (downloads > 0 ? $"\nАктивные загрузки ({downloads}) будут прерваны." : string.Empty);
        if (ChoiceDialog.Show(this, "Выход", text, ["Выйти", "Отмена"], 1, 1) != 0) return;
        foreach (var l in live) await _lifecycle.CloseAsync(l.ProfileId);
        _shutdownConfirmed = true;
        Close();
    }
}
