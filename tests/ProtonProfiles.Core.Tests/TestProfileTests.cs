using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class TestProfileTests
{
    [Theory]
    [InlineData("https://example.test/check?a=1#section")]
    [InlineData("http://localhost:8080/check")]
    [InlineData("https://[::1]:8443/")]
    [InlineData("HTTPS://EXAMPLE.TEST/path")]
    public void Test_profiles_open_arbitrary_web_sites_and_redirects_without_broadening_mail(string url)
    {
        var mail = new NavigationPolicy();
        var test = mail.ForProfile(new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Test", Kind = ProfileKind.Test, TestStartUrl = url });
        Assert.Equal(new Uri(url), test.StartUri);
        Assert.Equal(TopLevelDecision.Allow, test.EvaluateTopLevel(url));
        Assert.Equal(TopLevelDecision.Allow, test.EvaluateTopLevel("https://other.test/redirect"));
        Assert.Equal(TopLevelDecision.Allow, test.EvaluateTopLevel("http://127.0.0.1:8081/result"));
        Assert.Equal(TopLevelDecision.Allow, test.EvaluateTopLevel("about:blank"));
        Assert.Equal(TopLevelDecision.BlockOfferExternal, mail.EvaluateTopLevel("https://other.test/redirect"));
        Assert.Equal(NavigationPolicy.StartPage, mail.StartUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("data:text/html,test")]
    [InlineData("ms-settings:privacy")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://example.test/\n")]
    [InlineData(" https://example.test/")]
    [InlineData("")]
    public void Invalid_start_urls_are_rejected_and_never_written(string url)
    {
        using var env = new TestEnv();
        Assert.False(NavigationPolicy.IsValidTestStartUrl(url));
        Assert.False(env.Catalog.Create("Test", null, "#0891B2", out _, ProfileKind.Test, url).Saved);
        Assert.Empty(env.Repository.ListProfiles());
        var test = new NavigationPolicy().ForProfile(new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Test", Kind = ProfileKind.Test, TestStartUrl = "https://example.test/" });
        Assert.Equal(TopLevelDecision.Block, test.EvaluateTopLevel(url));
    }

    [Fact]
    public void Test_creation_has_fresh_storage_and_mail_cannot_receive_arbitrary_start_url()
    {
        using var env = new TestEnv();
        var mail = env.AddProfile();
        Assert.True(env.Catalog.Create("Test", null, "#0891B2", out var test, ProfileKind.Test, "https://example.test/").Saved);
        Assert.NotEqual(mail.Id, test!.Id);
        Assert.NotEqual(env.Paths.UserDataFolder(mail.Id), env.Paths.UserDataFolder(test.Id));
        Assert.False(Directory.Exists(env.Paths.UserDataFolder(test.Id)));
        Assert.NotEmpty(ProfileValidator.Validate(mail with { TestStartUrl = "https://example.test/" }));
        Assert.NotEmpty(ProfileValidator.Validate(mail with { Kind = (ProfileKind)99 }));
        Assert.NotEmpty(ProfileValidator.Validate(mail with { GraphicsPolicy = (GraphicsPolicy)99 }));
    }

    [Fact]
    public async Task Export_omits_test_url_by_default_and_import_keeps_test_blocked_until_configured()
    {
        using var env = new TestEnv();
        var test = env.AddProfile(change: p => p with { Kind = ProfileKind.Test, TestStartUrl = "https://private.test/check?token=synthetic#secret", GraphicsPolicy = GraphicsPolicy.BlockWebGlAndWebGpuExperimental });
        var safe = SettingsInterchange.Export([test]);
        Assert.DoesNotContain("private.test", safe);
        Assert.DoesNotContain("synthetic", safe);
        var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(safe));
        Assert.True(imported.Success);
        var p = imported.Preview!.Profiles[0];
        Assert.Equal(ProfileKind.Test, p.Kind);
        Assert.Null(p.TestStartUrl);
        Assert.Equal(test.GraphicsPolicy, p.GraphicsPolicy);
        env.Catalog.CommitImport(imported.Preview);
        var opened = await env.Lifecycle().OpenAsync(p.Id);
        Assert.Equal(OpenOutcome.Blocked, opened.Outcome);
        Assert.Equal(NetworkReadiness.TestUrlRequired, opened.Readiness);
        Assert.Equal(0, env.Engine.StartCalls);
        var full = SettingsInterchange.Export([test], new ExportOptions(IncludeTestStartUrls: true));
        Assert.Equal(test.TestStartUrl, SettingsInterchange.Import(Encoding.UTF8.GetBytes(full)).Preview!.Profiles[0].TestStartUrl);
        var old = JsonNode.Parse(SettingsInterchange.Export([env.AddProfile("Mail")]))!;
        var record = old["profiles"]![0]!.AsObject();
        record.Remove("profileKind"); record.Remove("testStartUrl"); record.Remove("graphicsPolicy");
        var oldProfile = SettingsInterchange.Import(Encoding.UTF8.GetBytes(old.ToJsonString())).Preview!.Profiles[0];
        Assert.Equal(ProfileKind.Mail, oldProfile.Kind);
        Assert.Equal(GraphicsPolicy.RuntimeDefault, oldProfile.GraphicsPolicy);
    }

    [Fact]
    public async Task Url_and_graphics_changes_require_restart_with_isolated_generation_snapshots()
    {
        using var env = new TestEnv();
        env.Engine.Capabilities = env.Engine.Capabilities with { GraphicsRestrictionSupported = true };
        var a = env.AddProfile(change: p => p with { Kind = ProfileKind.Test, TestStartUrl = "https://first.test/" });
        var b = env.AddProfile("Mail");
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(a.Id); await lifecycle.OpenAsync(b.Id);
        var edited = a with { TestStartUrl = "https://second.test/", GraphicsPolicy = GraphicsPolicy.BlockWebGlAndWebGpuExperimental };
        Assert.True(env.Catalog.SaveSettings(edited, profileIsLive: true).RestartRequired);
        Assert.Equal(a.TestStartUrl, env.Engine.Requests[0].Config.TestStartUrl);
        await lifecycle.RestartAsync(a.Id);
        Assert.Equal(edited.TestStartUrl, env.Engine.Requests[^1].Config.TestStartUrl);
        Assert.Equal(edited.GraphicsPolicy, env.Engine.Requests[^1].Config.GraphicsPolicy);
        Assert.Equal(0, env.Engine.Sessions[1].CloseCalls);
        Assert.True(ProfileConfig.RequiresRestart(a, a with { GraphicsPolicy = edited.GraphicsPolicy }));
        Assert.True(ProfileConfig.RequiresRestart(a, a with { TestStartUrl = edited.TestStartUrl }));
    }

    [Fact]
    public void Graphics_arguments_preserve_proxy_and_WebRTC_and_reject_unknown_policy()
    {
        ProxyEndpoint.TryParse("http://proxy.test:3128", out var proxy, out _);
        var args = BrowserArguments.Build(proxy, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental, GraphicsPolicy.BlockWebGlAndWebGpuExperimental);
        Assert.Contains("--disable-webgl", args.Split(' '));
        Assert.Contains("--disable-gpu", args.Split(' '));
        Assert.Contains("--disable-software-rasterizer", args.Split(' '));
        Assert.Contains("--disable-features=WebGPU,WebGPUService", args);
        Assert.Contains(BrowserArguments.WebRtcPolicyFlag, args);
        Assert.Contains("--proxy-server=http://proxy.test:3128", args);
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserArguments.Build(proxy, graphics: (GraphicsPolicy)99));
        using var env = new TestEnv();
        Assert.Equal(NetworkReadiness.UnsupportedGraphicsPolicy, NetworkReadinessEvaluator.Evaluate(env.AddProfile() with { GraphicsPolicy = GraphicsPolicy.BlockWebGlAndWebGpuExperimental }, env.Engine.Capabilities, env.Credentials));
    }

    [Fact]
    public void V3_migration_preserves_existing_profiles_permissions_snapshots_and_browser_data()
    {
        using var env = new TestEnv();
        var p = env.AddProfile(change: p => p with { BrowserTimeZoneId = "Europe/Berlin" });
        env.Repository.SetPermission(new PermissionDecision(p.Id, "https://mail.proton.me", PermissionKindKey.Notifications, PermissionChoice.Allow, DateTimeOffset.UnixEpoch));
        Directory.CreateDirectory(env.Paths.UserDataFolder(p.Id));
        var marker = Path.Combine(env.Paths.UserDataFolder(p.Id), "session-marker"); File.WriteAllText(marker, "keep");
        using (var c = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE Profile DROP COLUMN Kind; ALTER TABLE Profile DROP COLUMN TestStartUrl; ALTER TABLE Profile DROP COLUMN GraphicsPolicy; PRAGMA user_version = 3; UPDATE ProfileRevision SET Snapshot = json_remove(Snapshot, '$.Kind', '$.TestStartUrl', '$.GraphicsPolicy');";
            cmd.ExecuteNonQuery();
        }
        var migrated = new SqliteProfileRepository(env.Paths.DatabasePath, env.Paths.BackupsRoot);
        Assert.Equal(p, migrated.Get(p.Id));
        Assert.Equal(ProfileKind.Mail, migrated.GetRevisionSnapshot(p.Id, 1)!.Kind);
        Assert.Equal(GraphicsPolicy.RuntimeDefault, migrated.GetRevisionSnapshot(p.Id, 1)!.GraphicsPolicy);
        Assert.Single(migrated.ListPermissions(p.Id)); Assert.Equal("keep", File.ReadAllText(marker));
        var backup = Assert.Single(Directory.GetFiles(env.Paths.BackupsRoot, "profiles.v3.*.db"));
        using var bc = new SqliteConnection($"Data Source={backup};Pooling=False"); bc.Open();
        Assert.Equal(3, SqliteProfileRepository.ReadSchemaVersion(bc));
    }

    [Fact]
    public void General_diagnostics_exclude_test_urls_and_do_not_claim_verified_graphics_coverage()
    {
        using var env = new TestEnv();
        var p = env.AddProfile(change: p => p with { Kind = ProfileKind.Test, TestStartUrl = "https://private.test/?token=secret", GraphicsPolicy = GraphicsPolicy.BlockWebGlAndWebGpuExperimental });
        var text = DiagnosticsReport.Build(new EnvironmentInfo("test", "test", null, null, "test", "test", "test"), env.Engine.Capabilities, [(p, ProfileRuntimeState.Closed(p.Id))], []);
        Assert.DoesNotContain("private.test", text); Assert.DoesNotContain("secret", text);
        using var doc = JsonDocument.Parse(text);
        var profile = doc.RootElement.GetProperty("profiles")[0];
        Assert.Equal("Test", profile.GetProperty("profileKind").GetString());
        Assert.Equal("NotPerformed", profile.GetProperty("graphicsRuntimeCoverage").GetString());
    }
}
