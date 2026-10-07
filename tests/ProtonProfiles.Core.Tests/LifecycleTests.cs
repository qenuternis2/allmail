using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public class LifecycleTests
{
    [Fact]
    public async Task Open_creates_uuid_derived_udf_and_close_awaits_exit()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var svc = env.Lifecycle();

        var open = await svc.OpenAsync(a.Id);
        Assert.Equal(OpenOutcome.Opened, open.Outcome);
        Assert.Equal(LifecyclePhase.Open, svc.GetState(a.Id).Phase);
        var request = Assert.Single(env.Engine.Requests);
        Assert.Equal(env.Paths.UserDataFolder(a.Id), request.UserDataFolder);
        Assert.Contains(a.Id.ToString("D"), request.UserDataFolder);
        Assert.True(Directory.Exists(request.UserDataFolder));
        Assert.NotNull(env.Repository.Get(a.Id)!.LastOpenedAt);

        var close = await svc.CloseAsync(a.Id);
        Assert.Equal(CloseOutcome.Closed, close.Outcome);
        Assert.Equal(0, env.Engine.LiveProcesses());
        Assert.True(Directory.Exists(request.UserDataFolder), "closing must not remove browser data");
    }

    [Fact]
    public async Task Two_profiles_get_distinct_udfs_and_generations()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var b = env.AddProfile("B");
        var svc = env.Lifecycle();
        await svc.OpenAsync(a.Id);
        await svc.OpenAsync(b.Id);
        var udfs = env.Engine.Requests.Select(r => r.UserDataFolder).ToList();
        Assert.Equal(2, udfs.Distinct().Count());
        Assert.NotEqual(env.Engine.Requests[0].Context.GenerationId, env.Engine.Requests[1].Context.GenerationId);
    }

    [Fact]
    public async Task Duplicate_opens_coalesce_into_one_generation()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var svc = env.Lifecycle();
        env.Engine.StartGate = new TaskCompletionSource();
        var t1 = svc.OpenAsync(a.Id);
        var t2 = svc.OpenAsync(a.Id);
        env.Engine.StartGate.SetResult();
        await Task.WhenAll(t1, t2);
        Assert.Equal(1, env.Engine.StartCalls);
    }

    [Fact]
    public async Task Stale_generation_is_rejected_after_restart()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var svc = env.Lifecycle();
        await svc.OpenAsync(a.Id);
        var first = env.Engine.Requests[0].Context;
        Assert.True(svc.IsCurrentGeneration(first));
        await svc.RestartAsync(a.Id);
        var second = env.Engine.Requests[1].Context;
        Assert.False(svc.IsCurrentGeneration(first));
        Assert.True(svc.IsCurrentGeneration(second));
        Assert.False(env.Engine.Requests[0].IsCurrentGeneration(first));
    }

    [Fact]
    public async Task Cancelled_initialization_is_disposed_not_resurrected()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var svc = env.Lifecycle();
        env.Engine.StartGate = new TaskCompletionSource();
        var open = svc.OpenAsync(a.Id);
        await WaitUntil(() => env.Engine.StartCalls == 1);
        var close = svc.CloseAsync(a.Id);
        env.Engine.StartGate.SetResult();
        var openResult = await open;
        await close;
        Assert.Equal(OpenOutcome.Cancelled, openResult.Outcome);
        Assert.Equal(LifecyclePhase.Closed, svc.GetState(a.Id).Phase);
        Assert.Equal(0, env.Engine.LiveProcesses());
        Assert.All(env.Engine.Sessions, s => Assert.True(s.CloseCalls > 0));
    }

    [Fact]
    public async Task Exit_timeout_enters_recovery_keeps_lock_and_udf()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var svc = env.Lifecycle(exitTimeout: TimeSpan.FromMilliseconds(200));
        await svc.OpenAsync(a.Id);
        env.Engine.ExitOnClose = false;

        var close = await svc.CloseAsync(a.Id);
        Assert.Equal(CloseOutcome.RecoveryRequired, close.Outcome);
        Assert.Equal(LifecyclePhase.RecoveryRequired, svc.GetState(a.Id).Phase);
        Assert.True(Directory.Exists(env.Paths.UserDataFolder(a.Id)));
        // Lock still held: nobody else may reuse the UDF.
        Assert.False(ProfileLock.TryAcquire(env.Paths, a.Id, out _));
        // Reopen and delete are blocked.
        Assert.Equal(OpenOutcome.RecoveryRequired, (await svc.OpenAsync(a.Id)).Outcome);
        var delete = await svc.DeleteLocalProfileAsync(a.Id);
        Assert.False(delete.Completed);
        Assert.True(Directory.Exists(env.Paths.UserDataFolder(a.Id)));

        // The retained exit signal of the same generation completes recovery.
        env.Engine.Sessions[0].SignalExit();
        var retry = await svc.RetryRecoveryAsync(a.Id);
        Assert.Equal(CloseOutcome.Closed, retry.Outcome);
        Assert.True(ProfileLock.TryAcquire(env.Paths, a.Id, out var lk));
        lk!.Dispose();
    }

    [Fact]
    public async Task Unexpected_crash_closes_profile_without_touching_data_or_others()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var b = env.AddProfile("B");
        var svc = env.Lifecycle();
        await svc.OpenAsync(a.Id);
        await svc.OpenAsync(b.Id);
        File.WriteAllText(Path.Combine(env.Paths.UserDataFolder(a.Id), "marker"), "x");

        env.Engine.Sessions[0].SignalExit();
        await WaitUntil(() => svc.GetState(a.Id).Phase == LifecyclePhase.Closed);
        Assert.Equal(1, env.Engine.Sessions[0].CloseCalls);
        Assert.Equal(LifecyclePhase.Open, svc.GetState(b.Id).Phase);
        Assert.True(File.Exists(Path.Combine(env.Paths.UserDataFolder(a.Id), "marker")));
        Assert.Equal(OpenOutcome.Opened, (await svc.OpenAsync(a.Id)).Outcome);
    }

    [Fact]
    public async Task Fourth_profile_requires_explicit_choice_including_starting()
    {
        using var env = new TestEnv();
        var ids = Enumerable.Range(0, 4).Select(i => env.AddProfile("P" + i, p => i == 0 ? p with { IsPinned = true } : p).Id).ToList();
        var svc = env.Lifecycle();
        await svc.OpenAsync(ids[0]);
        await svc.OpenAsync(ids[1]);
        env.Engine.StartGate = new TaskCompletionSource();
        var third = svc.OpenAsync(ids[2]); // still starting
        var fourth = await svc.OpenAsync(ids[3]);
        Assert.Equal(OpenOutcome.CapacityReached, fourth.Outcome);
        Assert.Equal(3, fourth.Capacity!.AllLive.Count);
        Assert.DoesNotContain(fourth.Capacity.SuggestedToClose, l => l.ProfileId == ids[0]); // pinned not suggested
        Assert.DoesNotContain(fourth.Capacity.SuggestedToClose, l => l.ProfileId == ids[2]); // starting not suggested
        // Nothing was evicted.
        Assert.Equal(LifecyclePhase.Open, svc.GetState(ids[0]).Phase);
        Assert.Equal(LifecyclePhase.Open, svc.GetState(ids[1]).Phase);
        env.Engine.StartGate.SetResult();
        await third;

        await svc.CloseAsync(ids[1]);
        Assert.Equal(OpenOutcome.Opened, (await svc.OpenAsync(ids[3])).Outcome);
    }

    [Fact]
    public async Task Proxy_profile_is_blocked_in_core_build_and_never_opens_as_system()
    {
        using var env = new TestEnv();
        Model.ProxyEndpoint.TryCreate("http", "127.0.0.1", 8080, out var ep, out _);
        var a = env.AddProfile("A", p => p with { NetworkMode = NetworkMode.Proxy, Proxy = new ProxySettings(ep, ProxyAuthMode.None, null) });
        var svc = env.Lifecycle();
        var r = await svc.OpenAsync(a.Id);
        Assert.Equal(OpenOutcome.Blocked, r.Outcome);
        Assert.Equal(NetworkReadiness.UnsupportedInThisBuild, r.Readiness);
        Assert.Equal(0, env.Engine.StartCalls);
    }

    [Fact]
    public async Task Proxy_with_missing_credentials_is_blocked_in_experimental_build()
    {
        using var env = new TestEnv();
        env.Engine.Capabilities = env.Engine.Capabilities with { ProxySupport = ProxySupportLevel.ExperimentalBrowserFlag };
        Model.ProxyEndpoint.TryCreate("http", "proxy.test", 3128, out var ep, out _);
        var a = env.AddProfile("A", p => p with { NetworkMode = NetworkMode.Proxy, Proxy = new ProxySettings(ep, ProxyAuthMode.Basic, null) });
        var b = env.AddProfile("B", p => p with { NetworkMode = NetworkMode.Proxy, Proxy = new ProxySettings(null, ProxyAuthMode.None, null) });
        var svc = env.Lifecycle();
        Assert.Equal(NetworkReadiness.CredentialsRequired, (await svc.OpenAsync(a.Id)).Readiness);
        Assert.Equal(NetworkReadiness.EndpointRequired, (await svc.OpenAsync(b.Id)).Readiness);
        Assert.Equal(0, env.Engine.StartCalls);

        var reference = env.Credentials.Write(a.Id, new Credentials.ProxyCredential("u", "p"));
        env.Repository.Update(env.Repository.Get(a.Id)! with { Proxy = new ProxySettings(ep, ProxyAuthMode.Basic, reference) });
        Assert.Equal(OpenOutcome.Opened, (await svc.OpenAsync(a.Id)).Outcome);
    }

    [Fact]
    public async Task Unset_network_from_import_blocks_opening()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A", p => p with { NetworkMode = NetworkMode.Unset });
        var r = await env.Lifecycle().OpenAsync(a.Id);
        Assert.Equal(NetworkReadiness.NetworkModeRequired, r.Readiness);
        Assert.Equal(0, env.Engine.StartCalls);
    }

    [Fact]
    public async Task Reset_removes_only_udf_and_permissions_of_that_profile()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var b = env.AddProfile("B");
        var svc = env.Lifecycle();
        await svc.OpenAsync(a.Id);
        await svc.OpenAsync(b.Id);
        File.WriteAllText(Path.Combine(env.Paths.UserDataFolder(a.Id), "cookies"), "a");
        File.WriteAllText(Path.Combine(env.Paths.UserDataFolder(b.Id), "cookies"), "b");
        foreach (var id in new[] { a.Id, b.Id })
            env.Repository.SetPermission(new PermissionDecision(id, "https://mail.proton.me", PermissionKindKey.Notifications, PermissionChoice.Allow, DateTimeOffset.UtcNow));

        var report = await svc.ResetLocalSessionAsync(a.Id);
        Assert.True(report.Completed);
        Assert.False(Directory.Exists(env.Paths.UserDataFolder(a.Id)));
        Assert.Empty(env.Repository.ListPermissions(a.Id));
        Assert.NotNull(env.Repository.Get(a.Id)); // UUID and preferences preserved
        Assert.Equal("b", File.ReadAllText(Path.Combine(env.Paths.UserDataFolder(b.Id), "cookies")));
        Assert.Single(env.Repository.ListPermissions(b.Id));
        Assert.Equal(LifecyclePhase.Open, svc.GetState(b.Id).Phase);
        Assert.Empty(env.Repository.ListPendingOperations());
    }

    [Fact]
    public async Task Delete_removes_metadata_secrets_and_udf_but_not_external_downloads()
    {
        using var env = new TestEnv();
        var external = Path.Combine(env.Root, "UserDownloads");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "attachment.pdf"), "keep");
        var a = env.AddProfile("A", p => p with { DownloadDirectory = external });
        var b = env.AddProfile("B");
        var secret = env.Credentials.Write(a.Id, new Credentials.ProxyCredential("u", "p"));
        var svc = env.Lifecycle();
        await svc.OpenAsync(a.Id);

        var report = await svc.DeleteLocalProfileAsync(a.Id);
        Assert.True(report.Completed);
        Assert.Null(env.Repository.Get(a.Id));
        Assert.False(Directory.Exists(env.Paths.ProfileDirectory(a.Id)));
        Assert.False(env.Credentials.Exists(secret));
        Assert.True(File.Exists(Path.Combine(external, "attachment.pdf")));
        Assert.NotNull(env.Repository.Get(b.Id));
    }

    [Fact]
    public async Task Interrupted_delete_resumes_at_startup_without_opening()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        Directory.CreateDirectory(env.Paths.UserDataFolder(a.Id));
        env.Repository.AddPendingOperation(a.Id, PendingOperationKind.Delete, "Requested");
        var svc = env.Lifecycle();
        Assert.Equal(OpenOutcome.Blocked, (await svc.OpenAsync(a.Id)).Outcome);

        var results = await svc.ResumePendingOperationsAsync();
        Assert.Single(results);
        Assert.True(results[0].Report.Completed);
        Assert.Equal(0, env.Engine.StartCalls);
        Assert.Null(env.Repository.Get(a.Id));
        Assert.Empty(env.Repository.ListPendingOperations());
    }

    [Fact]
    public async Task Rapid_operations_never_leave_two_live_generations()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var svc = env.Lifecycle();
        var tasks = new List<Task>();
        for (var i = 0; i < 30; i++)
        {
            tasks.Add((i % 4) switch
            {
                0 => svc.OpenAsync(a.Id),
                1 => svc.CloseAsync(a.Id),
                2 => svc.RestartAsync(a.Id),
                _ => svc.OpenAsync(a.Id),
            });
        }
        await Task.WhenAll(tasks);
        Assert.True(env.Engine.LiveProcesses() <= 1);
        var state = svc.GetState(a.Id);
        Assert.Equal(state.Phase == LifecyclePhase.Open ? 1 : 0, env.Engine.LiveProcesses());
        await svc.CloseAsync(a.Id);
        Assert.Equal(0, env.Engine.LiveProcesses());
    }

    [Fact]
    public async Task Startup_failure_cause_and_runtime_survive_automatic_recovery_until_next_start()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        FakeSession? partial = null;
        const string error = "Ограничение WebGL не подтверждено. Доступны: OffscreenCanvas WebGL2.";
        env.Engine.FailWith = request =>
        {
            partial = new FakeSession(request.Context, env.Engine);
            return new BrowserStartException(error, processMayExist: true, partialSession: partial);
        };
        var svc = env.Lifecycle();
        Assert.Equal(OpenOutcome.RecoveryRequired, (await svc.OpenAsync(a.Id)).Outcome);
        partial!.SignalExit();
        await WaitUntil(() => svc.GetState(a.Id).Phase == LifecyclePhase.Closed);
        Assert.Equal(error, svc.GetState(a.Id).LastError);
        Assert.Equal(partial.RuntimeVersion, svc.GetState(a.Id).EffectiveRuntimeVersion);
        Assert.True(ProfileLock.TryAcquire(env.Paths, a.Id, out var profileLock));
        profileLock!.Dispose();
        env.Engine.FailWith = null;
        Assert.Equal(OpenOutcome.Opened, (await svc.OpenAsync(a.Id)).Outcome);
        Assert.Null(svc.GetState(a.Id).LastError);
        await svc.CloseAsync(a.Id);
    }

    [Fact]
    public async Task Engine_failure_with_possible_process_enters_recovery()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        env.Engine.FailWith = _ => new BrowserStartException("boom", processMayExist: true);
        var svc = env.Lifecycle();
        Assert.Equal(OpenOutcome.RecoveryRequired, (await svc.OpenAsync(a.Id)).Outcome);

        var b = env.AddProfile("B");
        env.Engine.FailWith = _ => new BrowserStartException("no runtime", processMayExist: false);
        Assert.Equal(OpenOutcome.Failed, (await svc.OpenAsync(b.Id)).Outcome);
        Assert.Equal(LifecyclePhase.Closed, svc.GetState(b.Id).Phase);
        Assert.True(ProfileLock.TryAcquire(env.Paths, b.Id, out var lk));
        lk!.Dispose();
    }

    [Fact]
    public async Task Repeated_open_close_cycles_leave_no_live_processes()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var svc = env.Lifecycle();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(OpenOutcome.Opened, (await svc.OpenAsync(a.Id)).Outcome);
            Assert.Equal(CloseOutcome.Closed, (await svc.CloseAsync(a.Id)).Outcome);
        }
        Assert.Equal(0, env.Engine.LiveProcesses());
        Assert.Equal(20, env.Engine.Sessions.Select(s => s.Context.GenerationId).Distinct().Count());
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
