using System.ComponentModel;
using System.Windows;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Downloads;

namespace ProtonProfiles.App.Controls;

public sealed class DownloadItem(DownloadInfo info) : INotifyPropertyChanged
{
    public DownloadInfo Info { get; private set; } = info;
    internal long Order { get; init; }
    public Guid Id => Info.DownloadId;
    public bool Pending => Info.Phase is DownloadPhase.InProgress or DownloadPhase.Paused || Info.Resume is not null;
    public string FileName => Info.FileName;
    public string? FilePath => Info.FilePath;
    public string Status => Info.Phase switch
    {
        DownloadPhase.Completed => "Готово",
        DownloadPhase.Cancelled => "Отменено",
        DownloadPhase.Interrupted => "Ошибка загрузки",
        DownloadPhase.Paused => "Приостановлено",
        _ when Info.BytesReceived == 0 => "Ожидание данных…",
        _ when Info.TotalBytes is > 0 && Info.BytesReceived >= Info.TotalBytes => "Сохранение файла…",
        _ => Info.TotalBytes is > 0 ? $"Загружается · {Math.Floor(Percent):0}%" : "Загружается",
    };
    public string Detail
    {
        get
        {
            var size = DownloadProgress.Size(Info.BytesReceived);
            var detail = Info.TotalBytes is > 0 ? size + " из " + DownloadProgress.Size(Info.TotalBytes.Value)
                : Info.Phase == DownloadPhase.Completed ? size : size + " · размер неизвестен";
            if (Info.Phase == DownloadPhase.InProgress && Info.TotalBytes is > 0 && Info.BytesReceived >= Info.TotalBytes)
                detail += " · ожидание завершения браузером";
            else if (Info.Phase == DownloadPhase.InProgress && Info.BytesPerSecond is { } speed)
            {
                detail += " · " + DownloadProgress.Size(speed) + "/с";
                if (Info.Remaining is { } time) detail += " · осталось " + DownloadProgress.Duration(time);
                else if (speed == 0 && Info.BytesReceived > 0) detail += " · ожидание данных";
            }
            return detail;
        }
    }
    public string? Reason => Info.Reason;
    public double Percent => Info.Phase == DownloadPhase.Completed ? 100 : DownloadProgress.Percent(Info.BytesReceived, Info.TotalBytes);
    public bool Indeterminate => Info.TotalBytes is not > 0 && Info.Phase == DownloadPhase.InProgress;
    public Visibility ProgressVisibility => Info.Phase is DownloadPhase.InProgress or DownloadPhase.Paused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PauseVisibility => Info.Pause is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ResumeVisibility => Info.Resume is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CancelVisibility => Info.Cancel is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FolderVisibility => Info.Phase == DownloadPhase.Completed && Info.FilePath is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReasonVisibility => string.IsNullOrEmpty(Reason) ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(DownloadInfo info) { Info = info; PropertyChanged?.Invoke(this, new(null)); }
}
