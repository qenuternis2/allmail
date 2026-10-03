using System.Diagnostics;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public class StorageTests
{
    [Fact]
    public void Paths_derive_only_from_uuid()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pp-root"));
        var paths = new ManagedPaths(root);
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(Path.Combine(root, "Profiles", id.ToString("D"), "WebViewData"), paths.UserDataFolder(id));
        Assert.Equal(Path.Combine(root, "Locks", id.ToString("D") + ".lock"), paths.LockFile(id));
        Assert.Throws<ArgumentException>(() => paths.UserDataFolder(Guid.Empty));
    }

    [Fact]
    public void Effective_udf_check_rejects_other_paths()
    {
        var paths = new ManagedPaths("/tmp/pp-root");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.True(paths.IsExpectedUserDataFolder(a, paths.UserDataFolder(a) + Path.DirectorySeparatorChar));
        Assert.False(paths.IsExpectedUserDataFolder(a, paths.UserDataFolder(b)));
        Assert.False(paths.IsExpectedUserDataFolder(a, "/tmp/somewhere/EBWebView"));
        Assert.False(paths.IsExpectedUserDataFolder(a, null));
    }

    [Fact]
    public void Lock_rejects_second_holder_and_releases()
    {
        using var env = new TestEnv();
        var id = Guid.NewGuid();
        Assert.True(ProfileLock.TryAcquire(env.Paths, id, out var first));
        Assert.False(ProfileLock.TryAcquire(env.Paths, id, out _));
        first!.Dispose();
        Assert.True(File.Exists(env.Paths.LockFile(id))); // stale file remains...
        Assert.True(ProfileLock.TryAcquire(env.Paths, id, out var again)); // ...but is not a live lock
        again!.Dispose();
    }

    [Fact]
    public async Task Lock_held_by_another_process_blocks_until_it_dies()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/flock")) return; // Linux-only harness; Windows covered by the app test plan
        using var env = new TestEnv();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(env.Paths.LocksRoot);
        var path = env.Paths.LockFile(id);
        File.WriteAllText(path, string.Empty);
        using var holder = Process.Start(new ProcessStartInfo("/usr/bin/flock", ["-x", path, "sleep", "30"]) { UseShellExecute = false })!;
        await LifecycleTests.WaitUntil(() => !ProfileLock.TryAcquire(env.Paths, id, out var probe) || !Release(probe));
        Assert.False(ProfileLock.TryAcquire(env.Paths, id, out _));
        holder.Kill(entireProcessTree: true); // simulated crash of the other instance
        holder.WaitForExit();
        await LifecycleTests.WaitUntil(() => ProfileLock.TryAcquire(env.Paths, id, out var l) && Release(l));
    }

    private static bool Release(ProfileLock? l) { l?.Dispose(); return l is not null; }

    [Fact]
    public void Reset_deleter_removes_udf_only()
    {
        using var env = new TestEnv();
        var id = Guid.NewGuid();
        var udf = env.Paths.UserDataFolder(id);
        Directory.CreateDirectory(Path.Combine(udf, "Default", "IndexedDB"));
        File.WriteAllText(Path.Combine(udf, "Default", "Cookies"), "x");
        File.WriteAllText(Path.Combine(env.Paths.ProfileDirectory(id), "sibling.txt"), "keep");
        var r = new SafeProfileDeleter(env.Paths).DeleteUserDataFolder(id);
        Assert.Equal(CleanupOutcome.Completed, r.Outcome);
        Assert.False(Directory.Exists(udf));
        Assert.True(File.Exists(Path.Combine(env.Paths.ProfileDirectory(id), "sibling.txt")));
    }

    [Fact]
    public void Links_inside_tree_are_removed_without_following()
    {
        using var env = new TestEnv();
        var id = Guid.NewGuid();
        var udf = env.Paths.UserDataFolder(id);
        Directory.CreateDirectory(udf);
        var outside = Path.Combine(env.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(udf, "link-to-outside"), outside);
        File.CreateSymbolicLink(Path.Combine(udf, "file-link"), Path.Combine(outside, "precious.txt"));

        var r = new SafeProfileDeleter(env.Paths).DeleteProfileDirectory(id);
        Assert.Equal(CleanupOutcome.Completed, r.Outcome);
        Assert.False(Directory.Exists(env.Paths.ProfileDirectory(id)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void Profile_directory_that_is_a_link_is_rejected()
    {
        using var env = new TestEnv();
        var id = Guid.NewGuid();
        var outside = Path.Combine(env.Root, "victim");
        Directory.CreateDirectory(Path.Combine(outside, "WebViewData"));
        File.WriteAllText(Path.Combine(outside, "WebViewData", "data.txt"), "keep");
        Directory.CreateSymbolicLink(env.Paths.ProfileDirectory(id), outside);

        var deleter = new SafeProfileDeleter(env.Paths);
        Assert.Equal(CleanupOutcome.RejectedUnsafePath, deleter.DeleteProfileDirectory(id).Outcome);
        Assert.Equal(CleanupOutcome.RejectedUnsafePath, deleter.DeleteUserDataFolder(id).Outcome);
        Assert.True(File.Exists(Path.Combine(outside, "WebViewData", "data.txt")));
    }

    [Fact]
    public void Ownership_rejects_paths_outside_the_profile()
    {
        using var env = new TestEnv();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var deleter = new SafeProfileDeleter(env.Paths);
        Assert.NotNull(deleter.ValidateOwnership(a, env.Paths.UserDataFolder(b)));
        Assert.NotNull(deleter.ValidateOwnership(a, Path.Combine(env.Paths.ProfileDirectory(a), "..", b.ToString("D"))));
        Assert.NotNull(deleter.ValidateOwnership(a, env.Paths.ProfilesRoot));
        Assert.NotNull(deleter.ValidateOwnership(a, env.Root));
        Assert.Null(deleter.ValidateOwnership(a, env.Paths.UserDataFolder(a)));
    }

    [Fact]
    public void Missing_directory_is_nothing_to_delete()
    {
        using var env = new TestEnv();
        Assert.Equal(CleanupOutcome.NothingToDelete, new SafeProfileDeleter(env.Paths).DeleteProfileDirectory(Guid.NewGuid()).Outcome);
    }
}
