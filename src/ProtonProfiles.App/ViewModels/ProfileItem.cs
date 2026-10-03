using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Reminders;

namespace ProtonProfiles.App.ViewModels;

public sealed class ProfileItem : INotifyPropertyChanged
{
    private ProfileConfig _config;
    private ProfileRuntimeState _state;
    private ReminderStatus _reminder = new(ReminderState.NoCheckRecorded, null, null);
    private NetworkReadiness _readiness;

    public ProfileItem(ProfileConfig config, ProfileRuntimeState state)
    {
        _config = config;
        _state = state;
    }

    public Guid Id => _config.Id;
    public ProfileConfig Config { get => _config; set { _config = value; Changed(null); } }
    public ProfileRuntimeState State { get => _state; set { _state = value; Changed(null); } }
    public ReminderStatus Reminder { get => _reminder; set { _reminder = value; Changed(null); } }
    public NetworkReadiness Readiness { get => _readiness; set { _readiness = value; Changed(null); } }

    public string DisplayName => _config.DisplayName;
    public string? EmailLabel => _config.Kind == ProfileKind.Test ? "Тестовый профиль" : _config.EmailLabel;
    public string FavoriteMark => _config.IsFavorite ? "★" : string.Empty;
    public string PinMark => _config.IsPinned ? "📌" : string.Empty;

    public Brush ColorBrush
    {
        get
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(_config.Color)); }
            catch (FormatException) { return Brushes.Gray; }
        }
    }

    public static string PhaseText(LifecyclePhase phase) => phase switch
    {
        LifecyclePhase.Closed => "Закрыт",
        LifecyclePhase.Starting => "Открывается",
        LifecyclePhase.Open => "Открыт",
        LifecyclePhase.Closing => "Закрывается",
        LifecyclePhase.RecoveryRequired => "Требуется восстановление",
        _ => phase.ToString(),
    };

    public string StatusText
    {
        get
        {
            var parts = new List<string> { PhaseText(_state.Phase) };
            if (_readiness != NetworkReadiness.Ready) parts.Add(NetworkReadinessEvaluator.Describe(_readiness));
            if (_config.PendingRevision is not null) parts.Add("ожидают применения настройки");
            if (!string.IsNullOrEmpty(_state.LastError)) parts.Add(_state.LastError!);
            return string.Join(" · ", parts);
        }
    }

    public string ReminderText => _config.Kind == ProfileKind.Test ? string.Empty : _reminder.State switch
    {
        ReminderState.NoCheckRecorded => "Дата проверки не записана",
        ReminderState.Due => $"Пора проверить ящик (срок {_reminder.DueDate:dd.MM.yyyy})",
        ReminderState.Snoozed => $"Напоминание отложено (срок {_reminder.DueDate:dd.MM.yyyy})",
        ReminderState.NotDue => $"Следующая проверка: {_reminder.DueDate:dd.MM.yyyy}",
        _ => string.Empty,
    };

    public bool ReminderDue => _config.Kind == ProfileKind.Mail && _reminder.State == ReminderState.Due;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
