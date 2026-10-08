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
using ProtonProfiles.App.Controls;
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
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App;

public partial class MainWindow : Window, IBrowserViewHost
{
    private readonly ManagedPaths _paths;
    private readonly MailfudGeoIpUpdater _geoIpUpdater;
    private readonly IProfileRepository _repository;
    private readonly ProfileCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly PermissionPolicy _permissions;
    private readonly string _runtimeVersion;
    private readonly ObservableCollection<ProfileItem> _items = [];
    private readonly Dictionary<Guid, ProfileBrowserTabs> _views = [];
    private readonly DownloadsPanel _downloads = new();
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private ProfileLifecycleService _lifecycle = null!;
    private WebView2Engine? _engine;
    private readonly Dictionary<Guid, ProfileDiagnosticsWindow> _diagnosticsWindows = [];
    private (GenerationContext Context, string Uri)? _pendingExternal;
    private bool _shutdownConfirmed;
    private bool _updatingGroupFilter;
    private GridLength _expandedProfilesWidth;
    private readonly double _expandedProfilesMinWidth;

    public MainWindow(ManagedPaths paths, IProfileRepository repository, ProfileCatalog catalog, ICredentialStore credentials, PermissionPolicy permissions, string runtimeVersion, MailfudGeoIpUpdater geoIpUpdater)
    {
        InitializeComponent();
        _expandedProfilesWidth = ProfilesColumn.Width;
        _expandedProfilesMinWidth = ProfilesColumn.MinWidth;
        DownloadsArea.Child = _downloads;
        _downloads.HideRequested += () => DownloadsArea.Visibility = Visibility.Collapsed;
        _downloads.BrowserDetailsRequested += OnDownloadDetails;
        _paths = paths;
        _geoIpUpdater = geoIpUpdater;
        _repository = repository;
        _catalog = catalog;
        _credentials = credentials;
        _permissions = permissions;
        _runtimeVersion = runtimeVersion;
        ProfileList.ItemsSource = _items;
        _reminderTimer.Tick += (_, _) => RefreshReminders();
        InitializePlacement();
    }

    public void Initialize(IBrowserEngine engine)
    {
        _lifecycle = new ProfileLifecycleService(_repository, engine, _credentials, _paths);
        _engine = engine as WebView2Engine;
        _lifecycle.StateChanged += state => Dispatcher.InvokeAsync(() => OnStateChanged(state));
        Title = "SecureBrowser"
            + " — " + FingerprintProbePage.ApplicationVersion;
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

    private void OnToggleProfilesPanel(object sender, RoutedEventArgs e)
    {
        var collapse = ProfilesPanel.Visibility == Visibility.Visible;
        if (collapse)
        {
            _expandedProfilesWidth = ProfilesColumn.Width;
            ProfilesColumn.MinWidth = 40;
            ProfilesColumn.Width = new GridLength(40);
        }
        else
        {
            ProfilesColumn.Width = _expandedProfilesWidth;
            ProfilesColumn.MinWidth = _expandedProfilesMinWidth;
        }
        ProfilesPanel.Visibility = ProfilesSplitter.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        CollapsedProfilesStrip.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
        (collapse ? ExpandProfilesButton : CollapseProfilesButton).Focus();
    }

    private void OnShowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Reload()
    {
        var selectedId = Selected?.Id;
        var query = SearchBox.Text;
        var groups = _repository.ListGroups(); var memberships = _repository.ListGroupAssignments();
        var all = _catalog.List(); var selectedGroup = GroupFilter.SelectedItem as GroupChoice;
        _updatingGroupFilter = true;
        try {
            var choices = new[] {new GroupChoice(null,$"Все профили ({all.Count})",true),new GroupChoice(null,$"Без группы ({all.Count(p=>!memberships.ContainsKey(p.Id))})")}
                .Concat(groups.Select(g=>new GroupChoice(g.Id,$"{g.Name} ({memberships.Values.Count(id=>id==g.Id)})"))).ToArray();
            GroupFilter.ItemsSource = choices;
            GroupFilter.SelectedItem = choices.FirstOrDefault(g=>selectedGroup is not null && g.Id==selectedGroup.Id && g.All==selectedGroup.All) ?? choices[0];
        } finally {_updatingGroupFilter=false;}
        var filter = (GroupChoice)GroupFilter.SelectedItem;
        var visible = ProfileCatalog.Filter(all, query).Where(p=>filter.All || (memberships.TryGetValue(p.Id,out var group)?group:(Guid?)null)==filter.Id).ToArray();
        var existing = _items.ToDictionary(item => item.Id);
        var visibleIds = visible.Select(profile => profile.Id).ToHashSet();
        // Preserve selected row/controller identity: clearing the directory hid and
        // reactivated a live WebView several times during each search keystroke.
        for (var index = _items.Count - 1; index >= 0; index--)
            if (!visibleIds.Contains(_items[index].Id)) _items.RemoveAt(index);
        for (var index = 0; index < visible.Length; index++)
        {
            var p = visible[index];
            var state = _lifecycle.GetState(p.Id);
            var groupLabel = memberships.TryGetValue(p.Id,out var group)?groups.FirstOrDefault(g=>g.Id==group)?.Name ?? "":"";
            var item = existing.GetValueOrDefault(p.Id);
            if (item is null || item.GroupLabel != groupLabel)
            {
                if (item is not null) _items.Remove(item);
                item = new ProfileItem(p, state) { GroupLabel = groupLabel };
            }
            if (item.Config != p) item.Config = p;
            if (item.State != state) item.State = state;
            Refresh(item);
            var oldIndex = _items.IndexOf(item);
            if (oldIndex < 0) _items.Insert(index, item);
            else if (oldIndex != index) _items.Move(oldIndex, index);
        }
        if (selectedId is not null) ProfileList.SelectedItem = _items.FirstOrDefault(i => i.Id == selectedId);
        UpdateSelectedPanel();
    }

    private void Refresh(ProfileItem item)
    {
        var reminder = ReminderCalculator.Evaluate(item.Config, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        var readiness = NetworkReadinessEvaluator.Evaluate(item.Config, _lifecycle.Capabilities, _credentials);
        if (item.Reminder != reminder) item.Reminder = reminder;
        if (item.Readiness != readiness) item.Readiness = readiness;
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

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (_lifecycle is not null) Reload(); }
    private void OnGroupFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {if(!_updatingGroupFilter && _lifecycle is not null)Reload();}
    private void OnGroups(object sender, RoutedEventArgs e) {new ProfileGroupsWindow(this,_repository).ShowDialog();Reload();}
    private void OnAssignGroup(object sender, RoutedEventArgs e)
    {
        var ids=ProfileList.SelectedItems.Cast<ProfileItem>().Select(p=>p.Id).ToArray(); if(ids.Length==0)return;
        var assignments=_repository.ListGroupAssignments(); var current=assignments.TryGetValue(ids[0],out var id)?id:(Guid?)null;
        if(!ProfileGroupsWindow.Choose(this,_repository.ListGroups(),ids.Length,current,out var group))return;
        try {_repository.AssignGroup(ids,group);}
        catch(ArgumentException error) {ChoiceDialog.Show(this,"Группа не изменена",error.Message,["ОК"],0,0);}
        Reload();
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        PlacementProfileChanged(Selected?.Id);
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
        _downloads.SelectProfile(s?.Id);
        UpdateDownloadsButton();
        ConfirmVisitButton.Visibility = RemindersButton.Visibility = s?.Config.Kind == ProfileKind.Mail ? Visibility.Visible : Visibility.Collapsed;
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
        SelectedStatus.Text = string.IsNullOrEmpty(s.ReminderText) ? s.StatusText : $"{s.StatusText} · {s.ReminderText}";
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
        var downloads = _downloads.ActiveCount(id);
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
        if (report.Completed) _downloads.ForgetProfile(s.Id);
        Reload();
        if (!report.Completed)
            ChoiceDialog.Show(this, "Удаление не завершено", (report.Message ?? string.Empty) + "\nОчистка будет продолжена при следующем запуске.", ["ОК"], 0, 0);
    }

    // ---------------- Metadata commands ----------------

    private async void OnResetPermissions(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        var live = _lifecycle.GetState(profile.Id).Phase != LifecyclePhase.Closed;
        var message = $"Удалить сохранённые приложением разрешения сайтов профиля «{profile.DisplayName}»?\nCookies, вход на сайты и вкладки сохранятся.";
        if (live) message += "\nПрофиль будет закрыт. Несохранённая работа может быть потеряна; активные загрузки будут прерваны.";
        if (ChoiceDialog.Show(this, "Сброс разрешений", message, ["Сбросить разрешения", "Отмена"], 1, 1) != 0) return;
        if (live && !await CloseProfileAsync(profile.Id, confirm: false)) return;
        try { _permissions.ResetProfile(profile.Id); StatusBarText.Text = $"Разрешения профиля «{profile.DisplayName}» сброшены."; }
        catch (Exception error) { ChoiceDialog.Show(this, "Разрешения не сброшены", error.Message, ["ОК"], 0, 0); }
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var palette = new[] { "#2563EB", "#059669", "#D97706", "#DC2626", "#7C3AED", "#0891B2", "#DB2777", "#4B5563" };
        var color = palette[_catalog.List().Count % palette.Length];
        var dialog = new NewProfileWindow(this, color, _repository.ListGroups(), (GroupFilter.SelectedItem as GroupChoice)?.Id);
        if (dialog.ShowDialog() != true || dialog.Result is not { } profile) return;
        var result = _catalog.Create(profile.DisplayName, profile.EmailLabel, profile.Color, out var created, profile.Kind, profile.TestStartUrl, dialog.GroupId);
        if (!result.Saved)
        {
            ChoiceDialog.Show(this, "Профиль не создан", string.Join("\n", result.Errors), ["ОК"], 0, 0);
            return;
        }
        SearchBox.Clear();
        _updatingGroupFilter=true;
        if(GroupFilter.SelectedItem is not GroupChoice filter || !filter.All && filter.Id!=dialog.GroupId)
            GroupFilter.SelectedItem=GroupFilter.Items.Cast<GroupChoice>().FirstOrDefault(g=>!g.All && g.Id==dialog.GroupId);
        _updatingGroupFilter=false;
        Reload();
        ProfileList.SelectedItem = _items.FirstOrDefault(i => i.Id == created!.Id);
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var current = _repository.Get(s.Id)!;
        var editor = new ProfileEditorWindow(this, current, _lifecycle.Capabilities, _paths, _geoIpUpdater);
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
                "Изменения URL, сети, User-Agent, языка, часового пояса или защиты вступят в силу после перезапуска профиля. Текущая страница будет закрыта.",
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
        if (!_views.TryGetValue(id, out var tabs)) return;
        try
        {
            tabs.HomeAddress = NavigationHome(config);
            if (_lifecycle.GetSession(id) is WebView2Session session)
            {
                if (session.Config is { } current) session.Config = current with { ColorScheme = config.ColorScheme, ZoomFactor = config.ZoomFactor };
                foreach (var view in session.Views)
                {
                    if (view.CoreWebView2 is null) continue;
                    view.CoreWebView2.Profile.PreferredColorScheme = WebView2Engine.ToApi(config.ColorScheme);
                    view.ZoomFactor = config.ZoomFactor;
                }
            }
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
        var include = ChoiceDialog.Show(this, "Экспорт настроек",
            "Экспортируются настройки без cookie, данных браузера и учётных данных прокси. По умолчанию адреса прокси и начальные URL исключаются. Полный начальный URL может содержать токены в пути или параметрах. Выберите, какие адреса включить:",
            ["Без адресов", "Прокси", "Тестовые URL", "Все адреса", "Отмена"], 0, 4);
        if (include is null or 4) return;
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "SecureBrowser-settings.json" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, _catalog.Export(new ExportOptions(include is 1 or 3, include is 2 or 3)), new UTF8Encoding(false));
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        ImportSettingsFile(dialog.FileName);
    }

    private void ImportSettingsFile(string fileName)
    {
        byte[] utf8;
        try
        {
            // Check/read the same handle; a selected file can disappear or change before it is opened.
            // Never allocate or read beyond the import limit plus one byte, including a growing file.
            using var input = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > SettingsInterchange.MaxFileBytes) throw new InvalidDataException();
            var buffer = new byte[checked((int)SettingsInterchange.MaxFileBytes + 1)];
            var count = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count > SettingsInterchange.MaxFileBytes) throw new InvalidDataException();
            utf8 = buffer[..count];
        }
        catch (InvalidDataException)
        {
            ChoiceDialog.Show(this, "Импорт", "Файл больше 1 МиБ.", ["ОК"], 0, 0);
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ChoiceDialog.Show(this, "Импорт отклонён",
                "Ничего не импортировано.\n\nНе удалось прочитать файл. Возможно, он удалён, занят другой программой или недоступен.", ["ОК"], 0, 0);
            return;
        }
        var result = _catalog.PreviewImport(utf8);
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
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "SecureBrowser-diagnostics.json" };
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
        if (!_views.TryGetValue(context.ProfileId, out var tabs))
        {
            var config = _repository.Get(context.ProfileId);
            tabs = new ProfileBrowserTabs(context, config is null ? NavigationPolicy.StartPage.AbsoluteUri : NavigationHome(config));
            _views.Add(context.ProfileId, tabs);
            BrowserArea.Children.Add(tabs);
            var owner = tabs;
            tabs.TabMoveRequested += (moved, index) => _lifecycle.IsCurrentGeneration(context)
                && _lifecycle.GetSession(context.ProfileId) is WebView2Session session && session.MoveTab(moved, index);
            tabs.NewTabRequested += async () =>
            {
                if (_engine is null || !_lifecycle.IsCurrentGeneration(context) || _lifecycle.GetSession(context.ProfileId) is not WebView2Session session) return;
                var opened = await _engine.OpenTabAsync(session);
                if (opened is not null && owner.ActiveView == opened) owner.FocusAddress();
            };
            tabs.CloseTabRequested += async closed =>
            {
                if (_lifecycle.IsCurrentGeneration(context) && _lifecycle.GetSession(context.ProfileId) is WebView2Session session)
                    await session.CloseTabAsync(closed);
            };
        }
        if (tabs.Context != context) throw new InvalidOperationException("Предыдущее поколение вкладок ещё не закрыто.");
        tabs.Add(view);
        UpdateSelectedPanel();
    }

    void IBrowserViewHost.Detach(GenerationContext context, WebView2 view)
    {
        if (_views.TryGetValue(context.ProfileId, out var tabs) && tabs.Context == context)
        {
            tabs.Remove(view);
            if (tabs.Count == 0)
            {
                BrowserArea.Children.Remove(tabs);
                _views.Remove(context.ProfileId);
                _downloads.CloseContext(context);
                UpdateDownloadsButton();
            }
        }
        UpdateSelectedPanel();
    }

    void IBrowserViewHost.TabReady(GenerationContext context, WebView2 view)
    {
        if (_views.TryGetValue(context.ProfileId, out var tabs) && tabs.Context == context) tabs.MarkReady(view);
    }

    WebView2? IBrowserViewHost.ActiveView(GenerationContext context) =>
        _views.TryGetValue(context.ProfileId, out var tabs) && tabs.Context == context ? tabs.ActiveView : null;

    void IBrowserViewHost.SelectTab(GenerationContext context, WebView2 view)
    {
        if (_views.TryGetValue(context.ProfileId, out var tabs) && tabs.Context == context) tabs.Select(view);
    }

    private static string NavigationHome(ProfileConfig config) => new NavigationPolicy().ForProfile(config).StartUri.AbsoluteUri;

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
        ExternalLinkText.Text = "Ссылка за пределами профиля: " + Redactor.RedactUrl(uri);
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
        return FinalizeDownloadPath(dialog.FileName);
    }

    // Keep final-name/overwrite decisions testable after the native picker returns.
    private string? FinalizeDownloadPath(string target)
    {
        var chosenDir = Path.GetDirectoryName(target)!;
        // Re-sanitize the user-edited name and keep the file inside the chosen folder.
        var final = Path.Combine(chosenDir, DownloadPaths.SanitizeFileName(Path.GetFileName(target)));
        if (!DownloadPaths.IsInside(chosenDir, final)) return null;
        if (!string.Equals(target, final, ManagedPaths.PathComparison) && File.Exists(final))
        {
            var choice = ChoiceDialog.Show(this, "Заменить существующий файл?",
                $"После обработки имени загрузка будет сохранена как «{Path.GetFileName(final)}». Этот файл уже существует. Заменить его?",
                ["Заменить", "Отмена"], 1, 1);
            if (choice != 0) return null;
        }
        return final;
    }

    private void OnDownloads(object sender, RoutedEventArgs e)
    {
        _downloads.SelectProfile(Selected?.Id);
        DownloadsArea.Visibility = DownloadsArea.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDownloadsButton()
    {
        var count = Selected is { } profile ? _downloads.ActiveCount(profile.Id) : 0;
        DownloadsButton.Content = count == 0 ? "Загрузки" : $"Загрузки · {count}";
        DownloadsButton.IsEnabled = Selected is not null;
    }

    private void OnDownloadDetails(DownloadInfo download)
    {
        var context = download.Context;
        if (Selected?.Id != context.ProfileId || !_lifecycle.IsCurrentGeneration(context)
            || _lifecycle.GetSession(context.ProfileId) is not WebView2Session session) return;
        try
        {
            if (!session.OpenDownloadDetails(download.DownloadId)) StatusBarText.Text = "Окно браузера недоступно; подробности загрузки не открыты.";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { StatusBarText.Text = "Не удалось открыть подробности загрузки браузера."; }
    }

    void IBrowserViewHost.ReportDownload(DownloadInfo info)
    {
        var state = _lifecycle.GetState(info.Context.ProfileId);
        if (!_lifecycle.IsCurrentGeneration(info.Context)
            && !(state.Generation == info.Context.GenerationId && state.Phase == LifecyclePhase.Closing)) return;
        var added = _downloads.Report(info);
        UpdateDownloadsButton();
        if (Selected?.Id != info.Context.ProfileId) return;
        if (added) DownloadsArea.Visibility = Visibility.Visible;
        if (info.Phase is DownloadPhase.Completed or DownloadPhase.Interrupted or DownloadPhase.Cancelled)
            StatusBarText.Text = $"{new DownloadItem(info).Status}: {info.FileName}" + (info.Reason is null ? "" : " · " + info.Reason);
    }

    void IBrowserViewHost.ReportProblem(GenerationContext context, string message)
    {
        var state = _lifecycle.GetState(context.ProfileId);
        if (!_lifecycle.IsCurrentGeneration(context)
            && !(state.Generation == context.GenerationId && state.Phase == LifecyclePhase.Closing)) return;
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
        if (_shutdownConfirmed) { SavePlacement(); base.OnClosing(e); return; }
        var live = _lifecycle.LiveProfiles();
        if (live.Count == 0) { SavePlacement(); base.OnClosing(e); return; }
        e.Cancel = true;
        var downloads = _catalog.List().Sum(profile => _downloads.ActiveCount(profile.Id));
        var text = $"Открыто профилей: {live.Count}. Закрыть приложение?\nНесохранённые черновики могут быть потеряны." + (downloads > 0 ? $"\nАктивные загрузки ({downloads}) будут прерваны." : string.Empty);
        if (ChoiceDialog.Show(this, "Выход", text, ["Выйти", "Отмена"], 1, 1) != 0) return;
        foreach (var l in live) await _lifecycle.CloseAsync(l.ProfileId);
        _shutdownConfirmed = true;
        Close();
    }
}
