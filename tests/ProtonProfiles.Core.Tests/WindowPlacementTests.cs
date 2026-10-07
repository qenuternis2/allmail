using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public class WindowPlacementTests
{
    [Fact]
    public void Placement_round_trips_without_creating_profile_data()
    {
        using var env = new TestEnv();
        var store = new WindowPlacementStore(env.Paths);
        var placement = new WindowBounds(-100, 40, 1280, 820, true);
        Assert.Null(store.Load());
        store.Save(placement);
        Assert.Equal(placement, store.Load());
        Assert.Empty(Directory.GetDirectories(env.Paths.ProfilesRoot));
        Assert.Empty(Directory.GetFiles(env.Paths.Root, "*.tmp"));
    }

    [Fact]
    public void Removed_monitor_and_oversized_window_are_fitted_into_work_area()
    {
        var fitted = WindowPlacementStore.Fit(new(-4000, 3000, 4000, 2000, true), new(0, 0, 1920, 1040, false), 900, 560);
        Assert.Equal(new WindowBounds(0, 0, 1920, 1040, true), fitted);
    }

    [Fact]
    public void Minimum_layout_and_negative_monitor_coordinates_are_preserved()
    {
        Assert.Equal(new WindowBounds(-1600, 0, 900, 560, false),
            WindowPlacementStore.Fit(new(-1800, -50, 200, 200, false), new(-1600, 0, 1600, 900, false), 900, 560));
    }

    [Fact]
    public void Invalid_or_oversized_state_is_ignored_and_invalid_save_preserves_previous_file()
    {
        using var env = new TestEnv();
        var store = new WindowPlacementStore(env.Paths);
        var valid = new WindowBounds(20, 30, 1000, 700, false);
        store.Save(valid);
        Assert.Throws<ArgumentException>(() => store.Save(valid with { Width = double.NaN }));
        Assert.Equal(valid, store.Load());
        File.WriteAllText(Path.Combine(env.Paths.Root, "window-placement.json"), "{broken");
        Assert.Null(store.Load());
        File.WriteAllText(Path.Combine(env.Paths.Root, "window-placement.json"), new string(' ', 4097));
        Assert.Null(store.Load());
    }
}
