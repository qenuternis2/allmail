using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Downloads;
using ProtonProfiles.Core.Lifecycle;

namespace ProtonProfiles.App.Browser;

/// <summary>One native operation, bounded progress notifications and lifetime tied to its originating tab.</summary>
internal sealed class BrowserDownloadTracker : IDisposable
{
    private CoreWebView2DownloadOperation _operation;
    private readonly string _uri;
    private readonly IBrowserViewHost _host;
    private readonly Func<bool> _current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _clock = new();
    private readonly DownloadRateSampler _rate = new();
    private DownloadInfo _last;
    private TimeSpan _lastSent = TimeSpan.FromSeconds(-1);
    private DownloadPhase? _lastPhase;
    private bool _paused, _cancelled, _closing, _disposed, _reported;
    public event Action? Stopped;
    public BrowserDownloadTracker(CoreWebView2DownloadOperation operation, IBrowserViewHost host, GenerationContext context, string path, Func<bool> current)
    {
        _operation = operation; _uri = operation.Uri; _host = host; _current = current;
        _last = new(context, Guid.NewGuid(), System.IO.Path.GetFileName(path), DownloadPhase.InProgress, FilePath: path);
        operation.StateChanged += StateChanged; operation.BytesReceivedChanged += BytesChanged;
        _timer.Tick += Tick;
    }
    public void Start() { _clock.Start(); _timer.Start(); Publish(true); }
    public string FilePath => _last.FilePath!;
    public bool MatchesResumption(CoreWebView2DownloadOperation operation, string path)
    {
        if (_disposed || _closing || !string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase)) return false;
        // Native retries raise DownloadStarting again with a replacement operation and the selected path.
        // A fresh request with the same URL has no received bytes and must still get its own save dialog.
        return operation.Uri == _uri && (operation.BytesReceived > 0 || _operation.State == CoreWebView2DownloadState.Interrupted);
    }
    public void ReplaceOperation(CoreWebView2DownloadOperation operation)
    {
        _operation.StateChanged -= StateChanged; _operation.BytesReceivedChanged -= BytesChanged;
        _operation = operation;
        operation.StateChanged += StateChanged; operation.BytesReceivedChanged += BytesChanged;
        Publish(true);
    }
    private void StateChanged(object? sender, object e) => Publish(true);
    private void BytesChanged(object? sender, object e) => Publish(false);
    private void Tick(object? sender, EventArgs e) => Publish(true);
    private void Publish(bool force)
    {
        if (_disposed || _closing) return;
        if (!_current()) { Close("Профиль или вкладка закрыты."); return; }
        if (!force && _clock.Elapsed - _lastSent < TimeSpan.FromMilliseconds(250)) return;
        var previous = _last;
        try
        {
            var bytes = Math.Max(0, _operation.BytesReceived);
            var advertised = _operation.TotalBytesToReceive;
            long? total = advertised is > 0 && advertised <= long.MaxValue ? (long)advertised.Value : null;
            var state = _operation.State;
            var reason = state == CoreWebView2DownloadState.Interrupted ? _operation.InterruptReason : CoreWebView2DownloadInterruptReason.None;
            var phase = state == CoreWebView2DownloadState.Completed ? DownloadPhase.Completed
                : _cancelled ? DownloadPhase.Cancelled
                : state == CoreWebView2DownloadState.Interrupted && (_paused || reason == CoreWebView2DownloadInterruptReason.UserPaused) ? DownloadPhase.Paused
                : state == CoreWebView2DownloadState.Interrupted && reason is CoreWebView2DownloadInterruptReason.UserCanceled or CoreWebView2DownloadInterruptReason.UserShutdown ? DownloadPhase.Cancelled
                : state == CoreWebView2DownloadState.Interrupted ? DownloadPhase.Interrupted : DownloadPhase.InProgress;
            if (_lastPhase != phase) { _rate.Reset(bytes, _clock.Elapsed); _lastPhase = phase; }
            var speed = phase == DownloadPhase.InProgress ? _rate.Sample(bytes, _clock.Elapsed) : null;
            var canResume = phase is DownloadPhase.Paused or DownloadPhase.Interrupted && _operation.CanResume;
            _last = _last with
            {
                Phase = phase, BytesReceived = bytes, TotalBytes = total, BytesPerSecond = speed,
                Remaining = DownloadRateSampler.Remaining(bytes, total, speed),
                Reason = phase == DownloadPhase.Interrupted ? Explain(reason) : phase == DownloadPhase.Cancelled ? "Загрузка отменена." : null,
                Pause = phase == DownloadPhase.InProgress ? () => Command(() => { _paused = true; _operation.Pause(); }) : null,
                Resume = canResume ? () => Command(() => { if (!_operation.CanResume) return; _paused = false; _operation.Resume(); }) : null,
                Cancel = phase is DownloadPhase.InProgress or DownloadPhase.Paused || canResume ? () => Command(() => { _cancelled = true; _operation.Cancel(); }) : null,
            };
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { Close("Окно браузера недоступно; загрузка остановлена."); return; }
        _lastSent = _clock.Elapsed;
        if (_reported && SameStatus(previous, _last)) return;
        _reported = true;
        _host.ReportDownload(_last);
        if (_last.Phase is DownloadPhase.Completed or DownloadPhase.Cancelled) Dispose();
    }
    private static bool SameStatus(DownloadInfo a, DownloadInfo b) => a.Phase == b.Phase && a.BytesReceived == b.BytesReceived
        && a.TotalBytes == b.TotalBytes && a.BytesPerSecond == b.BytesPerSecond && a.Remaining == b.Remaining && a.Reason == b.Reason
        && (a.Pause is null) == (b.Pause is null) && (a.Resume is null) == (b.Resume is null) && (a.Cancel is null) == (b.Cancel is null);
    private void Command(Action action)
    {
        // Never call SDK methods inside a WebView2 callback or after the originating controller closes.
        _ = _timer.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _closing) return;
            if (!_current()) { Close("Профиль или вкладка закрыты."); return; }
            try { action(); }
            catch (Exception e) when (e is COMException or InvalidOperationException) { Close("Не удалось продолжить загрузку; окно браузера недоступно."); return; }
            Publish(true);
        }), DispatcherPriority.Background);
    }
    public void Close(string reason)
    {
        if (_disposed || _closing) return;
        _closing = true;
        try { _operation.Cancel(); } catch (Exception e) when (e is COMException or InvalidOperationException) { }
        _host.ReportDownload(_last with { Phase = DownloadPhase.Cancelled, Reason = reason, Pause = null, Resume = null, Cancel = null, BytesPerSecond = null, Remaining = null });
        Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _timer.Stop(); _timer.Tick -= Tick;
        try { _operation.StateChanged -= StateChanged; _operation.BytesReceivedChanged -= BytesChanged; }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
        Stopped?.Invoke();
    }
    private static string Explain(CoreWebView2DownloadInterruptReason reason) => reason switch
    {
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "Нет доступа к папке или файлу. Проверьте разрешения.",
        CoreWebView2DownloadInterruptReason.FileNoSpace => "На диске недостаточно свободного места.",
        CoreWebView2DownloadInterruptReason.FileNameTooLong => "Слишком длинное имя или путь файла.",
        CoreWebView2DownloadInterruptReason.FileTooLarge => "Файл слишком большой для выбранного диска.",
        CoreWebView2DownloadInterruptReason.FileMalicious => "Браузер обнаружил опасный файл и заблокировал загрузку.",
        CoreWebView2DownloadInterruptReason.FileBlockedByPolicy => "Загрузка заблокирована политикой безопасности.",
        CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed => "Файл не прошёл проверку безопасности.",
        CoreWebView2DownloadInterruptReason.FileTooShort or CoreWebView2DownloadInterruptReason.ServerContentLengthMismatch => "Сервер передал неполный файл.",
        CoreWebView2DownloadInterruptReason.FileHashMismatch => "Контрольная сумма файла не совпала.",
        CoreWebView2DownloadInterruptReason.NetworkTimeout => "Сервер не ответил вовремя. Проверьте соединение или прокси.",
        CoreWebView2DownloadInterruptReason.NetworkDisconnected => "Соединение с сетью потеряно.",
        CoreWebView2DownloadInterruptReason.NetworkFailed or CoreWebView2DownloadInterruptReason.NetworkServerDown => "Не удалось получить файл. Проверьте соединение, прокси и доступность сервера.",
        CoreWebView2DownloadInterruptReason.NetworkInvalidRequest => "Не удалось отправить запрос загрузки серверу.",
        CoreWebView2DownloadInterruptReason.ServerFailed => "Сервер вернул ошибку при скачивании.",
        CoreWebView2DownloadInterruptReason.ServerNoRange => "Сервер не поддерживает продолжение. Начните скачивание заново.",
        CoreWebView2DownloadInterruptReason.ServerBadContent => "Сервер отправил некорректное содержимое файла.",
        CoreWebView2DownloadInterruptReason.ServerUnexpectedResponse => "Сервер вернул неожиданный ответ.",
        CoreWebView2DownloadInterruptReason.ServerCrossOriginRedirect => "Сервер перенаправил загрузку на другой сайт; браузер остановил скачивание.",
        CoreWebView2DownloadInterruptReason.ServerUnauthorized => "Сервер требует входа в аккаунт.",
        CoreWebView2DownloadInterruptReason.ServerForbidden => "Сервер запретил скачивание файла.",
        CoreWebView2DownloadInterruptReason.ServerCertificateProblem => "Ошибка сертификата сервера.",
        CoreWebView2DownloadInterruptReason.DownloadProcessCrashed => "Процесс загрузки завершился с ошибкой.",
        CoreWebView2DownloadInterruptReason.FileFailed or CoreWebView2DownloadInterruptReason.FileTransientError => "Не удалось записать файл на диск.",
        _ => "Загрузка прервана. Попробуйте продолжить или скачать файл заново.",
    };
}
