using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Lifecycle;

namespace ProtonProfiles.App.Controls;

/// <summary>In-memory history, scoped to profiles and keyed by operation rather than progress notifications.</summary>
public partial class DownloadsPanel : UserControl
{
    private readonly Dictionary<Guid, DownloadItem> _items = [];
    private readonly ObservableCollection<DownloadItem> _visible = [];
    private Guid? _profile;
    private long _nextOrder;
    public event Action? HideRequested;
    internal event Action<GenerationContext>? BrowserDetailsRequested;
    public DownloadsPanel() { InitializeComponent(); Files.ItemsSource = _visible; Refresh(); }
    public int ActiveCount(Guid id) => _items.Values.Count(i => i.Info.Context.ProfileId == id && i.Pending);
    public IReadOnlyList<DownloadItem> Items(Guid id) => _items.Values.Where(i => i.Info.Context.ProfileId == id).ToArray();
    public void SelectProfile(Guid? profile) { if (_profile == profile) return; _profile = profile; _visible.Clear(); foreach (var item in _items.Values.Where(i => i.Info.Context.ProfileId == profile).OrderByDescending(i => i.Order)) _visible.Add(item); Refresh(); }
    public bool Report(DownloadInfo info)
    {
        var added = !_items.TryGetValue(info.DownloadId, out var item);
        if (added) { item = new(info) { Order = ++_nextOrder }; _items.Add(info.DownloadId, item); if (info.Context.ProfileId == _profile) _visible.Insert(0, item); }
        else item!.Update(info);
        // Retain at most 100 finished entries per profile; active transfers are never discarded.
        foreach (var old in _items.Values.Where(i => i.Info.Context.ProfileId == info.Context.ProfileId && !i.Pending).OrderByDescending(i => i.Order).Skip(100).ToArray()) Remove(old);
        Refresh(); return added;
    }
    public void CloseContext(GenerationContext context)
    {
        foreach (var item in _items.Values.Where(i => i.Info.Context == context && i.Pending).ToArray())
            item.Update(item.Info with { Phase = DownloadPhase.Cancelled, Reason = "Профиль закрыт.", Pause = null, Resume = null, Cancel = null, BytesPerSecond = null, Remaining = null });
        Refresh();
    }
    public void ForgetProfile(Guid profile) { foreach (var item in Items(profile)) Remove(item); Refresh(); }
    private void Remove(DownloadItem item) { _items.Remove(item.Id); _visible.Remove(item); }
    private void Refresh() { EmptyMessage.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed; Files.Visibility = _visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible; ClearButton.IsEnabled = _visible.Any(i => !i.Pending); }
    private void OnClear(object sender, RoutedEventArgs e) { foreach (var item in _visible.Where(i => !i.Pending).ToArray()) Remove(item); Refresh(); }
    private void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void OnPause(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: DownloadItem item }) item.Info.Pause?.Invoke(); }
    private void OnResume(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: DownloadItem item }) item.Info.Resume?.Invoke(); }
    private void OnCancel(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: DownloadItem item }) item.Info.Cancel?.Invoke(); }
    private void OnBrowserDetails(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadItem item } && item.BrowserDetailsVisibility == Visibility.Visible)
            BrowserDetailsRequested?.Invoke(item.Info.Context);
    }
    private void OnFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DownloadItem item } || item.FilePath is not { } path) return;
        try { if (Path.GetDirectoryName(path) is { } directory && Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { MessageBox.Show(Window.GetWindow(this), "Не удалось открыть папку файла.", "SecureBrowser", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
