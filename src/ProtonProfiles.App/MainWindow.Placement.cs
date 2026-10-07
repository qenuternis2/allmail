using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.App;

public partial class MainWindow
{
    private WindowPlacementStore _placementStore = null!;
    private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Guid? _placementProfile;
    private bool _placementReady;
    private bool _restoringPlacement;

    private void InitializePlacement()
    {
        _placementStore = new(_paths);
        if (_placementStore.Load() is { } saved) RestorePlacement(saved);
        Loaded += (_, _) => _placementReady = true;
        _placementTimer.Tick += (_, _) => { _placementTimer.Stop(); SavePlacement(); };
        LocationChanged += (_, _) => QueuePlacementSave();
        SizeChanged += (_, _) => QueuePlacementSave();
        StateChanged += (_, _) => QueuePlacementSave();
        Closed += (_, _) => { _placementReady = false; _placementTimer.Stop(); _reminderTimer.Stop(); };
    }

    private void QueuePlacementSave()
    {
        if (!_placementReady || _restoringPlacement || WindowState == WindowState.Minimized) return;
        _placementTimer.Stop(); _placementTimer.Start();
    }

    private void PlacementProfileChanged(Guid? profileId)
    {
        if (_placementProfile == profileId) return;
        SavePlacement();
        _placementProfile = profileId;
        if (profileId is { } id && _repository.Get(id)?.WindowBounds is { } saved
            && WindowPlacementStore.IsValid(saved)) RestorePlacement(saved);
    }

    private void RestorePlacement(WindowBounds saved)
    {
        var area = SystemParameters.WorkArea;
        var fitted = WindowPlacementStore.Fit(saved, new(area.Left, area.Top, area.Width, area.Height, false), MinWidth, MinHeight);
        _restoringPlacement = true;
        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowState = WindowState.Normal;
            Left = fitted.Left; Top = fitted.Top; Width = fitted.Width; Height = fitted.Height;
            if (fitted.Maximized) WindowState = WindowState.Maximized;
        }
        finally { _restoringPlacement = false; }
    }

    private void SavePlacement()
    {
        if (!_placementReady || _restoringPlacement || WindowState == WindowState.Minimized) return;
        var rectangle = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        var bounds = new WindowBounds(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height, WindowState == WindowState.Maximized);
        if (!WindowPlacementStore.IsValid(bounds)) return;
        try
        {
            _placementStore.Save(bounds);
            if (_placementProfile is { } id && _repository.Get(id) is { } profile && profile.WindowBounds != bounds)
                _repository.Update(profile with { WindowBounds = bounds });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { StatusBarText.Text = "Не удалось сохранить размер и положение окна."; }
    }
}
