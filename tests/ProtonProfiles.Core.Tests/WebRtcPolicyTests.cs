using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class WebRtcPolicyTests
{
    [Fact]
    public void Defaults_and_old_imports_block_pages_without_claiming_network_enforcement()
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Synthetic" };
        Assert.Equal(WebRtcPagePolicy.Block, p.WebRtcPagePolicy);
        Assert.Equal(WebRtcNetworkPolicy.RuntimeDefault, p.WebRtcNetworkPolicy);
        var old = JsonNode.Parse(SettingsInterchange.Export([p]))!;
        var item = old["profiles"]![0]!.AsObject();
        item.Remove("webRtcPagePolicy");
        item.Remove("webRtcNetworkPolicy");
        var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(old.ToJsonString()));
        Assert.True(imported.Success);
        Assert.Equal(WebRtcPagePolicy.Block, imported.Preview!.Profiles[0].WebRtcPagePolicy);
        Assert.Equal(WebRtcNetworkPolicy.RuntimeDefault, imported.Preview.Profiles[0].WebRtcNetworkPolicy);
    }

    [Theory]
    [InlineData("webRtcPagePolicy", "block")]
    [InlineData("webRtcPagePolicy", "0")]
    [InlineData("webRtcPagePolicy", "Unknown")]
    [InlineData("webRtcNetworkPolicy", "--disable-web-security")]
    [InlineData("webRtcNetworkPolicy", "1")]
    public void Import_rejects_unrecognized_policies(string field, string value)
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Synthetic" };
        var doc = JsonNode.Parse(SettingsInterchange.Export([p]))!;
        doc["profiles"]![0]![field] = value;
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(doc.ToJsonString())).Success);
    }

    [Fact]
    public async Task Imported_experimental_policy_roundtrips_but_blocks_unsupported_build()
    {
        using var env = new TestEnv();
        var p = env.AddProfile(change: p => p with { WebRtcPagePolicy = WebRtcPagePolicy.Allow,
            WebRtcNetworkPolicy = WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental });
        var text = SettingsInterchange.Export([p]);
        Assert.DoesNotContain("verification", text, StringComparison.OrdinalIgnoreCase);
        var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(text));
        Assert.True(imported.Success);
        Assert.Equal(p.WebRtcNetworkPolicy, imported.Preview!.Profiles[0].WebRtcNetworkPolicy);
        Assert.Equal(p.WebRtcPagePolicy, imported.Preview.Profiles[0].WebRtcPagePolicy);
        var result = await env.Lifecycle().OpenAsync(p.Id);
        Assert.Equal(OpenOutcome.Blocked, result.Outcome);
        Assert.Equal(0, env.Engine.StartCalls);
        env.Engine.Capabilities = env.Engine.Capabilities with { WebRtcNetworkRestrictionSupported = true };
        Assert.Equal(NetworkReadiness.Ready, NetworkReadinessEvaluator.Evaluate(p, env.Engine.Capabilities, env.Credentials));
    }

    [Fact]
    public void Route_flag_is_opt_in_and_preserves_proxy()
    {
        ProxyEndpoint.TryParse("http://proxy.example:3128", out var proxy, out _);
        Assert.Equal(string.Empty, BrowserArguments.Build(null));
        Assert.Equal("--proxy-server=http://proxy.example:3128", BrowserArguments.Build(proxy));
        var restricted = BrowserArguments.Build(proxy, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental);
        Assert.Equal(BrowserArguments.WebRtcPolicyFlag + " --proxy-server=http://proxy.example:3128", restricted);
        Assert.Single(restricted.Split(' '), a => a.StartsWith("--force-webrtc", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserArguments.Build(proxy, (WebRtcNetworkPolicy)99));
    }

    [Theory]
    [InlineData(PermissionKindKey.Camera)]
    [InlineData(PermissionKindKey.Microphone)]
    public async Task Block_policy_overrides_remembered_allow_without_deleting_it(PermissionKindKey kind)
    {
        using var env = new TestEnv();
        var p = env.AddProfile();
        env.Repository.SetPermission(new PermissionDecision(p.Id, "https://mail.proton.me", kind, PermissionChoice.Allow, DateTimeOffset.UtcNow));
        var policy = new PermissionPolicy(env.Repository);
        var asked = false;
        Assert.False(await policy.ResolveAsync(new GenerationContext(p.Id, 1), "https://mail.proton.me", kind,
            (_, _) => { asked = true; return Task.FromResult<UserPermissionAnswer?>(UserPermissionAnswer.AlwaysAllow); }, p.WebRtcPagePolicy));
        Assert.False(asked);
        Assert.Equal(PermissionChoice.Allow, env.Repository.GetPermission(p.Id, "https://mail.proton.me", kind)!.Choice);
        Assert.Equal(PolicyVerdict.Allow, policy.Evaluate(p.Id, "https://mail.proton.me", kind, WebRtcPagePolicy.Allow));
    }

    [Fact]
    public async Task Page_policy_change_restarts_one_generation_and_keeps_snapshot_immutable()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var b = env.AddProfile("B");
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(a.Id);
        await lifecycle.OpenAsync(b.Id);
        var saved = env.Catalog.SaveSettings(env.Repository.Get(a.Id)! with { WebRtcPagePolicy = WebRtcPagePolicy.Allow }, profileIsLive: true);
        Assert.True(saved.RestartRequired);
        Assert.Equal(WebRtcPagePolicy.Block, env.Engine.Requests[0].Config.WebRtcPagePolicy);
        await lifecycle.RestartAsync(a.Id);
        Assert.True(env.Engine.Sessions[0].ProcessExited.IsCompleted);
        Assert.Equal(WebRtcPagePolicy.Allow, env.Engine.Requests[^1].Config.WebRtcPagePolicy);
        Assert.Equal(0, env.Engine.Sessions[1].CloseCalls);
        Assert.True(ProfileConfig.RequiresRestart(a, a with { WebRtcNetworkPolicy = WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental }));
    }

    [Fact]
    public void Migration_preserves_metadata_permissions_snapshots_and_browser_data()
    {
        using var env = new TestEnv();
        var p = env.AddProfile("Keep");
        env.Repository.SetPermission(new PermissionDecision(p.Id, "https://mail.proton.me", PermissionKindKey.Camera, PermissionChoice.Allow, DateTimeOffset.UnixEpoch));
        var folder = env.Paths.UserDataFolder(p.Id);
        Directory.CreateDirectory(folder);
        var marker = Path.Combine(folder, "synthetic-session");
        File.WriteAllText(marker, "keep");
        using (var c = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE ProfileGroupMember; DROP TABLE ProfileGroup; ALTER TABLE Profile DROP COLUMN Kind; ALTER TABLE Profile DROP COLUMN TestStartUrl; ALTER TABLE Profile DROP COLUMN GraphicsPolicy; ALTER TABLE Profile DROP COLUMN BrowserTimeZoneId; ALTER TABLE Profile DROP COLUMN WebRtcPagePolicy; ALTER TABLE Profile DROP COLUMN WebRtcNetworkPolicy; PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "UPDATE ProfileRevision SET Snapshot = json_remove(Snapshot, '$.WebRtcPagePolicy', '$.WebRtcNetworkPolicy');";
            cmd.ExecuteNonQuery();
        }
        var migrated = new SqliteProfileRepository(env.Paths.DatabasePath, env.Paths.BackupsRoot);
        Assert.Equal(p, migrated.Get(p.Id));
        Assert.Equal(WebRtcPagePolicy.Block, migrated.GetRevisionSnapshot(p.Id, 1)!.WebRtcPagePolicy);
        Assert.Single(migrated.ListPermissions(p.Id));
        Assert.Equal("keep", File.ReadAllText(marker));
        var backup = Assert.Single(Directory.GetFiles(env.Paths.BackupsRoot, "profiles.v1.*.db"));
        using var backupConnection = new SqliteConnection($"Data Source={backup};Pooling=False");
        backupConnection.Open();
        Assert.Equal(1, SqliteProfileRepository.ReadSchemaVersion(backupConnection));
        // Explicit choices survive a later reopen.
        migrated.Update(p with { WebRtcPagePolicy = WebRtcPagePolicy.Allow, WebRtcNetworkPolicy = WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental });
        var reopened = new SqliteProfileRepository(env.Paths.DatabasePath, env.Paths.BackupsRoot);
        Assert.Equal(WebRtcPagePolicy.Allow, reopened.Get(p.Id)!.WebRtcPagePolicy);
        Assert.Equal(WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental, reopened.Get(p.Id)!.WebRtcNetworkPolicy);
    }

    [Fact]
    public void Undefined_typed_policies_are_rejected_and_guard_resource_is_available()
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Synthetic" };
        Assert.NotEmpty(ProfileValidator.Validate(p with { WebRtcPagePolicy = (WebRtcPagePolicy)99 }));
        Assert.NotEmpty(ProfileValidator.Validate(p with { WebRtcNetworkPolicy = (WebRtcNetworkPolicy)99 }));
        Assert.Contains("RTCPeerConnection", WebRtcPageGuard.Script);
    }

    [Fact]
    public async Task Late_guard_failure_does_not_close_new_generation_and_current_failure_is_recorded()
    {
        using var env = new TestEnv();
        var p = env.AddProfile();
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(p.Id);
        var old = env.Engine.Sessions[0].Context;
        await lifecycle.RestartAsync(p.Id);
        var result = await lifecycle.StopGenerationAsync(old, "stale failure");
        Assert.Equal(CloseOutcome.AlreadyClosed, result.Outcome);
        Assert.Equal(0, env.Engine.Sessions[1].CloseCalls);
        await lifecycle.StopGenerationAsync(env.Engine.Sessions[1].Context, "guard failure");
        Assert.Equal(LifecyclePhase.Closed, lifecycle.GetState(p.Id).Phase);
        Assert.Equal("guard failure", lifecycle.GetState(p.Id).LastError);
    }

    [Fact]
    public async Task Guard_failure_exit_timeout_keeps_recovery_and_profile_lock()
    {
        using var env = new TestEnv();
        env.Engine.ExitOnClose = false;
        var p = env.AddProfile();
        var lifecycle = env.Lifecycle(TimeSpan.FromMilliseconds(20));
        await lifecycle.OpenAsync(p.Id);
        var result = await lifecycle.StopGenerationAsync(env.Engine.Sessions[0].Context, "guard failure");
        Assert.Equal(CloseOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(LifecyclePhase.RecoveryRequired, lifecycle.GetState(p.Id).Phase);
        Assert.Contains("guard failure", lifecycle.GetState(p.Id).LastError);
        Assert.False(ProtonProfiles.Core.Storage.ProfileLock.TryAcquire(env.Paths, p.Id, out var other));
        Assert.Null(other);
        env.Engine.Sessions[0].SignalExit();
        await lifecycle.CloseAsync(p.Id);
    }
}
