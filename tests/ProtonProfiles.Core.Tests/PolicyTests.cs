using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Downloads;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class ProxyTests
{
    [Theory]
    [InlineData("http://proxy.example:8080", "proxy.example", 8080)]
    [InlineData("HTTP://Proxy.Example.:3128/", "proxy.example", 3128)]
    [InlineData("http://127.0.0.1:1", "127.0.0.1", 1)]
    [InlineData("http://[::1]:8888", "::1", 8888)]
    [InlineData("http://пример.рф:8080", "xn--e1afmkfd.xn--p1ai", 8080)]
    public void Parses_and_normalizes(string input, string host, int port)
    {
        Assert.True(ProxyEndpoint.TryParse(input, out var ep, out var err), err);
        Assert.Equal(host, ep!.Host);
        Assert.Equal(port, ep.Port);
    }

    [Theory]
    [InlineData("http://user:pass@proxy:8080")]
    [InlineData("http://proxy:8080/path")]
    [InlineData("http://proxy:8080?x=1")]
    [InlineData("http://proxy:8080#f")]
    [InlineData("socks5://proxy:1080")]
    [InlineData("https://proxy:443")]
    [InlineData("http://proxy")]
    [InlineData("http://proxy:0")]
    [InlineData("http://proxy:70000")]
    [InlineData("http://proxy:8080 --disable-web-security")]
    [InlineData("http://proxy:8080\"--foo")]
    [InlineData("http://proxy:8080;direct://")]
    [InlineData("")]
    [InlineData("proxy:8080")]
    public void Rejects_unsafe_or_unsupported(string input) => Assert.False(ProxyEndpoint.TryParse(input, out _, out _));

    [Fact]
    public void Flag_is_built_from_parsed_fields_only()
    {
        ProxyEndpoint.TryParse("http://Proxy.Example:8080", out var ep, out _);
        Assert.Equal("--proxy-server=http://proxy.example:8080", ProxyArguments.BuildProxyServerFlag(ep!));
        ProxyEndpoint.TryParse("http://[::1]:8080", out var v6, out _);
        Assert.Equal("--proxy-server=http://[::1]:8080", ProxyArguments.BuildProxyServerFlag(v6!));
        Assert.Equal("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", BrowserArguments.Build(null, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental));
        Assert.Equal("--force-webrtc-ip-handling-policy=disable_non_proxied_udp --proxy-server=http://proxy.example:8080", BrowserArguments.Build(ep, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental));
    }

    [Theory]
    [InlineData("http://proxy.example:8080", true, ProxyChallengeMatcher.Decision.ReleaseProxyCredentials)]
    [InlineData("http://PROXY.example:8080/", true, ProxyChallengeMatcher.Decision.ReleaseProxyCredentials)]
    [InlineData("http://proxy.example:8081", true, ProxyChallengeMatcher.Decision.CancelMismatch)]
    [InlineData("https://proxy.example:8080", true, ProxyChallengeMatcher.Decision.CancelMismatch)]
    [InlineData("http://proxy.example.evil.test:8080", true, ProxyChallengeMatcher.Decision.CancelMismatch)]
    [InlineData("https://unrelated.test/login", true, ProxyChallengeMatcher.Decision.CancelMismatch)]
    [InlineData("http://u:p@proxy.example:8080", true, ProxyChallengeMatcher.Decision.CancelMismatch)]
    [InlineData("https://unrelated.test/login", false, ProxyChallengeMatcher.Decision.NotAProxyChallenge)]
    public void Credentials_are_released_only_to_the_exact_proxy(string challenge, bool proxyMode, ProxyChallengeMatcher.Decision expected)
    {
        ProxyEndpoint.TryParse("http://proxy.example:8080", out var ep, out _);
        Assert.Equal(expected, ProxyChallengeMatcher.Evaluate(ep, challenge, proxyMode));
    }

    [Fact]
    public void Auth_retry_is_bounded()
    {
        var budget = new ProxyAuthRetryBudget();
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume());
        Assert.True(budget.Exhausted);
    }
}

public class ValidationTests
{
    [Theory]
    [InlineData("ru-RU", true)]
    [InlineData("en", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("es-419", true)]
    [InlineData("de-DE-1996", true)]
    [InlineData("ru_RU", false)]
    [InlineData("r", false)]
    [InlineData("", false)]
    [InlineData("en-US; DROP", false)]
    public void Language_tags(string tag, bool ok) => Assert.Equal(ok, ProfileValidator.IsWellFormedLanguageTag(tag));

    [Fact]
    public void User_agent_rules()
    {
        Assert.Null(ProfileValidator.ValidateUserAgent("Mozilla/5.0 Test"));
        Assert.NotNull(ProfileValidator.ValidateUserAgent("a\r\nX-Injected: 1"));
        Assert.NotNull(ProfileValidator.ValidateUserAgent(new string('a', 513)));
        Assert.NotNull(ProfileValidator.ValidateUserAgent(""));
    }

    [Fact]
    public void Numeric_limits()
    {
        Assert.NotNull(ProfileValidator.ValidateZoom(double.NaN));
        Assert.NotNull(ProfileValidator.ValidateZoom(double.PositiveInfinity));
        Assert.NotNull(ProfileValidator.ValidateZoom(0.49));
        Assert.Null(ProfileValidator.ValidateZoom(2.0));
        Assert.NotNull(ProfileValidator.ValidateReminderMonths(0));
        Assert.Null(ProfileValidator.ValidateReminderMonths(12));
        Assert.NotNull(ProfileValidator.ValidateDisplayName(new string('x', 101)));
        Assert.Null(ProfileValidator.ValidateDisplayName("  ok  "));
        Assert.NotNull(ProfileValidator.ValidateColor("#12345"));
    }

    [Fact]
    public void Restart_required_only_for_engine_level_settings()
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "A" };
        Assert.False(ProfileConfig.RequiresRestart(p, p with { DisplayName = "B", ColorScheme = ColorSchemePreference.Dark, ZoomFactor = 1.5, ReminderMonths = 3, IsFavorite = true }));
        Assert.True(ProfileConfig.RequiresRestart(p, p with { UserAgentMode = UserAgentMode.Custom, CustomUserAgent = "x" }));
        Assert.True(ProfileConfig.RequiresRestart(p, p with { LanguageMode = LanguageMode.Custom, LanguageTag = "en-US" }));
        Assert.True(ProfileConfig.RequiresRestart(p, p with { ScriptLocaleMode = ScriptLocaleMode.MatchBrowserLanguage }));
        Assert.True(ProfileConfig.RequiresRestart(p, p with { NetworkMode = NetworkMode.Unset }));
    }

    [Fact]
    public void Script_locale_resolution()
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "A" };
        Assert.Null(p.ResolveScriptLocale("ru-RU"));
        Assert.Equal("ru-RU", (p with { ScriptLocaleMode = ScriptLocaleMode.MatchBrowserLanguage }).ResolveScriptLocale("ru-RU"));
        Assert.Equal("en-GB", (p with { ScriptLocaleMode = ScriptLocaleMode.MatchBrowserLanguage, LanguageMode = LanguageMode.Custom, LanguageTag = "en-GB" }).ResolveScriptLocale("ru-RU"));
        Assert.Equal("de-DE", (p with { ScriptLocaleMode = ScriptLocaleMode.Custom, ScriptLocaleTag = "de-DE" }).ResolveScriptLocale("ru-RU"));
    }
}

public class SettingsRevisionTests
{
    [Fact]
    public void Live_restart_change_becomes_pending_and_revert_restores_last_applied()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        env.Repository.Update(env.Repository.Get(a.Id)! with { LastAppliedRevision = 1 });
        var edited = env.Repository.Get(a.Id)! with { LanguageMode = LanguageMode.Custom, LanguageTag = "en-US" };
        var r = env.Catalog.SaveSettings(edited, profileIsLive: true);
        Assert.True(r.RestartRequired);
        var stored = env.Repository.Get(a.Id)!;
        Assert.Equal(2, stored.ConfigRevision);
        Assert.Equal(2, stored.PendingRevision);
        Assert.Equal(1, stored.LastAppliedRevision);

        Assert.True(env.Catalog.RevertToLastApplied(a.Id));
        var reverted = env.Repository.Get(a.Id)!;
        Assert.Equal(LanguageMode.System, reverted.LanguageMode);
        Assert.Null(reverted.PendingRevision);
        Assert.Equal(3, reverted.ConfigRevision);
    }

    [Fact]
    public async Task Successful_restart_commits_pending_revision()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var svc = env.Lifecycle();
        await svc.OpenAsync(a.Id);
        env.Catalog.SaveSettings(env.Repository.Get(a.Id)! with { UserAgentMode = UserAgentMode.Custom, CustomUserAgent = "Test/1" }, profileIsLive: true);
        Assert.NotNull(env.Repository.Get(a.Id)!.PendingRevision);
        await svc.RestartAsync(a.Id);
        var p = env.Repository.Get(a.Id)!;
        Assert.Null(p.PendingRevision);
        Assert.Equal(p.ConfigRevision, p.LastAppliedRevision);
        Assert.Equal("Test/1", env.Engine.Requests[^1].Config.CustomUserAgent);
        Assert.Null(env.Engine.Requests[0].Config.CustomUserAgent);
    }

    [Fact]
    public void Invalid_edit_never_replaces_active_revision()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var r = env.Catalog.SaveSettings(env.Repository.Get(a.Id)! with { LanguageMode = LanguageMode.Custom, LanguageTag = "bad tag" }, profileIsLive: false);
        Assert.False(r.Saved);
        Assert.Equal(1, env.Repository.Get(a.Id)!.ConfigRevision);
    }
}

public class PermissionTests
{
    [Fact]
    public async Task Decisions_are_per_profile_and_only_always_is_stored()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var b = env.AddProfile("B");
        var policy = new PermissionPolicy(env.Repository);
        var ctxA = new GenerationContext(a.Id, 1);
        var ctxB = new GenerationContext(b.Id, 2);

        Assert.True(await policy.ResolveAsync(ctxA, "https://mail.proton.me/u/0/inbox", PermissionKindKey.Notifications, (_, _) => Task.FromResult<UserPermissionAnswer?>(UserPermissionAnswer.AllowOnce)));
        Assert.Empty(env.Repository.ListPermissions(a.Id));

        Assert.True(await policy.ResolveAsync(ctxA, "https://mail.proton.me/", PermissionKindKey.Notifications, (_, _) => Task.FromResult<UserPermissionAnswer?>(UserPermissionAnswer.AlwaysAllow)));
        Assert.Equal(PolicyVerdict.Allow, policy.Evaluate(a.Id, "https://mail.proton.me/other", PermissionKindKey.Notifications));
        // Another generation of A (after restart) still sees the stored choice.
        Assert.Equal(PolicyVerdict.Allow, policy.Evaluate(a.Id, "https://mail.proton.me/", PermissionKindKey.Notifications));
        // B is unaffected.
        Assert.Equal(PolicyVerdict.AskUser, policy.Evaluate(b.Id, "https://mail.proton.me/", PermissionKindKey.Notifications));
        var asked = false;
        Assert.False(await policy.ResolveAsync(ctxB, "https://mail.proton.me/", PermissionKindKey.Notifications, (_, _) => { asked = true; return Task.FromResult<UserPermissionAnswer?>(null); }));
        Assert.True(asked);

        policy.ResetProfile(a.Id);
        Assert.Equal(PolicyVerdict.AskUser, policy.Evaluate(a.Id, "https://mail.proton.me/", PermissionKindKey.Notifications));
    }

    [Fact]
    public async Task Concurrent_frame_and_top_level_events_prompt_once()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var policy = new PermissionPolicy(env.Repository);
        var ctx = new GenerationContext(a.Id, 1);
        var prompts = 0;
        var gate = new TaskCompletionSource<UserPermissionAnswer?>();
        Task<UserPermissionAnswer?> Ask(string o, PermissionKindKey k) { Interlocked.Increment(ref prompts); return gate.Task; }
        var t1 = policy.ResolveAsync(ctx, "https://mail.proton.me/", PermissionKindKey.Camera, Ask, WebRtcPagePolicy.Allow);
        var t2 = policy.ResolveAsync(ctx, "https://mail.proton.me/frame", PermissionKindKey.Camera, Ask, WebRtcPagePolicy.Allow);
        gate.SetResult(UserPermissionAnswer.DenyOnce);
        Assert.False(await t1);
        Assert.False(await t2);
        Assert.Equal(1, prompts);
    }

    [Theory]
    [InlineData("https://mail.proton.me/", PermissionKindKey.Unknown)]
    [InlineData("https://mail.proton.me/", PermissionKindKey.Other)]
    [InlineData("file:///c:/x", PermissionKindKey.Notifications)]
    [InlineData("not a uri", PermissionKindKey.Camera)]
    public void Unknown_permissions_and_origins_default_to_deny(string uri, PermissionKindKey kind)
    {
        using var env = new TestEnv();
        Assert.Equal(PolicyVerdict.Deny, new PermissionPolicy(env.Repository).Evaluate(Guid.NewGuid(), uri, kind));
    }

    [Fact]
    public void Camera_microphone_geolocation_are_never_auto_granted()
    {
        using var env = new TestEnv();
        var policy = new PermissionPolicy(env.Repository);
        foreach (var k in new[] { PermissionKindKey.Camera, PermissionKindKey.Microphone, PermissionKindKey.Geolocation })
            Assert.Equal(PolicyVerdict.AskUser, policy.Evaluate(Guid.NewGuid(), "https://mail.proton.me", k, WebRtcPagePolicy.Allow));
    }
}

public class NavigationTests
{
    private readonly NavigationPolicy _policy = new();

    [Theory]
    [InlineData("https://mail.proton.me/u/0/inbox", TopLevelDecision.Allow)]
    [InlineData("https://account.proton.me/login", TopLevelDecision.Allow)]
    [InlineData("https://MAIL.proton.me/", TopLevelDecision.Allow)]
    [InlineData("about:blank", TopLevelDecision.Allow)]
    [InlineData("https://mail.proton.me.evil.test/", TopLevelDecision.BlockOfferExternal)]
    [InlineData("https://evilmail.proton.me.test/", TopLevelDecision.BlockOfferExternal)]
    [InlineData("https://example.com/?next=mail.proton.me", TopLevelDecision.BlockOfferExternal)]
    [InlineData("https://mail.proton.me:8443/", TopLevelDecision.BlockOfferExternal)]
    [InlineData("http://mail.proton.me/", TopLevelDecision.BlockOfferExternal)]
    [InlineData("https://user@mail.proton.me/", TopLevelDecision.Block)]
    [InlineData("javascript:alert(1)", TopLevelDecision.Block)]
    [InlineData("file:///C:/Windows/System32/calc.exe", TopLevelDecision.Block)]
    [InlineData("ms-settings:privacy", TopLevelDecision.Block)]
    public void Top_level_uses_exact_hosts(string uri, TopLevelDecision expected) => Assert.Equal(expected, _policy.EvaluateTopLevel(uri));

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com/a", true)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("file:///C:/x", false)]
    [InlineData("https://u:p@example.com", false)]
    [InlineData("search-ms:query=x", false)]
    public void External_launch_only_http_https(string uri, bool ok) => Assert.Equal(ok, NavigationPolicy.IsExternalLaunchable(uri));
}

public class DownloadTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("a<b>c:d|e?f*.txt", "a_b_c_d_e_f_.txt")]
    [InlineData("trailing. . ", "trailing")]
    [InlineData("", "attachment")]
    [InlineData("...", "attachment")]
    [InlineData("invoice\u202Etxt.exe", "invoice_txt.exe")]
    public void Sanitizes_server_names(string input, string expected) => Assert.Equal(expected, DownloadPaths.SanitizeFileName(input));

    [Fact]
    public void Collision_appends_counter()
    {
        var existing = new HashSet<string> { Path.Combine("/d", "a.pdf"), Path.Combine("/d", "a (1).pdf") };
        Assert.Equal(Path.Combine("/d", "a (2).pdf"), DownloadPaths.ResolveCollision("/d", "a.pdf", existing.Contains));
    }

    [Fact]
    public void Inside_check()
    {
        Assert.True(DownloadPaths.IsInside("/d", "/d/a.pdf"));
        Assert.False(DownloadPaths.IsInside("/d", "/d/../e/a.pdf"));
        Assert.False(DownloadPaths.IsInside("/d", "/dd/a.pdf"));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Redactor_strips_secrets_and_urls()
    {
        var text = "GET https://mail.proton.me/api/core/v4/users?token=abc123#frag Authorization: Bearer xyz Cookie: Session-Id=s3cr3t password=hunter2 user me@example.com";
        var r = Redactor.Redact(text);
        Assert.DoesNotContain("abc123", r);
        Assert.DoesNotContain("xyz", r);
        Assert.DoesNotContain("s3cr3t", r);
        Assert.DoesNotContain("hunter2", r);
        Assert.DoesNotContain("me@example.com", r);
        Assert.Contains("mail.proton.me", r);
    }

    [Fact]
    public void Report_contains_versions_but_no_identifiers_or_secrets()
    {
        ProxyEndpoint.TryCreate("http", "corp-proxy.internal", 3128, out var ep, out _);
        var id = Guid.NewGuid();
        var p = new ProfileConfig
        {
            Id = id, DisplayName = "Личный ящик", EmailLabel = "me@proton.me", NetworkMode = NetworkMode.Proxy,
            Proxy = new ProxySettings(ep, ProxyAuthMode.Basic, "ProtonProfiles/proxy/ref-123"), DownloadDirectory = "/home/me/private",
        };
        var state = ProfileRuntimeState.Closed(id) with { LastError = "failed https://mail.proton.me/x?token=t0k3n" };
        var json = DiagnosticsReport.Build(
            new EnvironmentInfo("0.1.0", "Core", "1.0.4258.31", "141.0.0.0", "Windows 11", ".NET 10", "X64"),
            new BrowserCapabilities("WebView2", true, ProxySupportLevel.None, true, true, true, true, true, true),
            [(p, state)],
            [new CheckResult("A01", EvidenceStatus.NotPerformed, "n/a", "Windows not available")]);
        Assert.Contains("1.0.4258.31", json);
        Assert.Contains("141.0.0.0", json);
        foreach (var secret in new[] { id.ToString(), "Личный", "me@proton.me", "corp-proxy", "ref-123", "/home/me", "t0k3n" })
            Assert.DoesNotContain(secret, json);
    }
}

public class FixtureNavigationTests
{
    [Fact]
    public void Fixture_origins_require_https_and_exact_port()
    {
        var p = new NavigationPolicy(["https://localhost:8443", "https://127.0.0.1:8444"], new Uri("https://localhost:8443/"));
        Assert.Equal(TopLevelDecision.Allow, p.EvaluateTopLevel("https://localhost:8443/a"));
        Assert.Equal(TopLevelDecision.Allow, p.EvaluateTopLevel("https://127.0.0.1:8444/b"));
        Assert.Equal(TopLevelDecision.BlockOfferExternal, p.EvaluateTopLevel("https://localhost:9999/"));
        Assert.Equal(TopLevelDecision.BlockOfferExternal, p.EvaluateTopLevel("https://localhost/"));
        Assert.Throws<ArgumentException>(() => new NavigationPolicy(["http://localhost:8080"]));
        Assert.Throws<ArgumentException>(() => new NavigationPolicy(null, new Uri("https://example.com/")));
    }
}
