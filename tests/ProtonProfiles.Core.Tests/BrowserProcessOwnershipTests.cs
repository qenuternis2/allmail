using System.Diagnostics;
using System.Text.Json;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public class BrowserProcessOwnershipTests
{
    [Fact]
    public async Task Recorded_browser_outlives_host_handle_and_releases_ownership_after_exit()
    {
        using var env = new TestEnv(); var id = Guid.NewGuid();
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"])
            : new ProcessStartInfo("/bin/sleep", ["30"]);
        start.UseShellExecute = false;
        using var child = Process.Start(start)!;
        try
        {
            Assert.True(ProfileLock.TryAcquire(env.Paths, id, out var host));
            using (host) host!.RecordBrowserProcess(child.Id);
            Assert.True(File.Exists(OwnerFile(env, id)));
            var premature = ProfileLock.TryAcquire(env.Paths, id, out var blocked);
            using (blocked) Assert.False(premature);
            child.Kill(); await child.WaitForExitAsync();
            var recovered = ProfileLock.TryAcquire(env.Paths, id, out var released);
            using (released) Assert.True(recovered);
            Assert.False(File.Exists(OwnerFile(env, id)));
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }

    private static string OwnerFile(TestEnv env, Guid id) => env.Paths.LockFile(id) + ".browser.json";
    private static void Write(TestEnv env, Guid id, int processId, long start, int version = 1)
    {
        Directory.CreateDirectory(env.Paths.LocksRoot);
        File.WriteAllText(OwnerFile(env, id), JsonSerializer.Serialize(new { Version = version, ProcessId = processId, StartTimeUtcTicks = start }));
    }

    [Fact]
    public void Live_browser_identity_blocks_ownership_without_a_host_file_handle()
    {
        using var env = new TestEnv(); var id = Guid.NewGuid();
        using var process = Process.GetCurrentProcess();
        Write(env, id, process.Id, process.StartTime.ToUniversalTime().Ticks);
        var before = File.ReadAllText(OwnerFile(env, id));
        var acquired = ProfileLock.TryAcquire(env.Paths, id, out var held);
        using (held) Assert.False(acquired);
        Assert.Equal(before, File.ReadAllText(OwnerFile(env, id)));
    }

    [Fact]
    public void Reused_pid_with_a_different_creation_time_releases_stale_ownership()
    {
        using var env = new TestEnv(); var id = Guid.NewGuid();
        using var process = Process.GetCurrentProcess();
        Write(env, id, process.Id, process.StartTime.ToUniversalTime().Ticks - 1);
        var acquired = ProfileLock.TryAcquire(env.Paths, id, out var held);
        using (held) Assert.True(acquired);
        Assert.False(File.Exists(OwnerFile(env, id)));
    }

    [Fact]
    public void Malformed_owner_record_refuses_to_truncate_the_original_lock()
    {
        using var env = new TestEnv(); var id = Guid.NewGuid();
        Directory.CreateDirectory(env.Paths.LocksRoot);
        File.WriteAllText(env.Paths.LockFile(id), "previous-owner");
        File.WriteAllText(OwnerFile(env, id), "{");
        var acquired = ProfileLock.TryAcquire(env.Paths, id, out var held);
        using (held) Assert.False(acquired);
        Assert.Equal("previous-owner", File.ReadAllText(env.Paths.LockFile(id)));
        Assert.Equal("{", File.ReadAllText(OwnerFile(env, id)));
    }

    [Fact]
    public void Unknown_owner_version_cannot_be_treated_as_a_dead_process()
    {
        using var env = new TestEnv(); var id = Guid.NewGuid();
        Write(env, id, int.MaxValue, 1, version: 99);
        var acquired = ProfileLock.TryAcquire(env.Paths, id, out var held);
        using (held) Assert.False(acquired);
        Assert.True(File.Exists(OwnerFile(env, id)));
    }
}
