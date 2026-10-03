using System.Text.Json;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class ScreenPrivacyTests
{
    private static Dictionary<string, object?> Matching(double zoom = 1) => new() {
        ["screenApisAvailable"] = true, ["width"] = 1920, ["height"] = 1080,
        ["availWidth"] = 1920, ["availHeight"] = 1080, ["availLeft"] = 0, ["availTop"] = 0,
        ["devicePixelRatio"] = zoom, ["orientationAngle"] = 0, ["orientationType"] = "landscape-primary",
        ["deviceWidthMatches"] = true, ["deviceHeightMatches"] = true, ["resolutionMatches"] = true,
    };

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(2)]
    public void Explicit_matching_metrics_and_media_queries_are_verified(double zoom) =>
        Assert.Equal(GraphicsReadbackOutcome.Verified, ScreenPrivacy.ReadResult(JsonSerializer.Serialize(Matching(zoom)), zoom).Outcome);

    [Fact]
    public void Missing_malformed_or_unreadable_evidence_never_confirms_emulation()
    {
        foreach (var json in new string?[] { null, "null", "[]", "{}", "{broken", "{\"screenApisAvailable\":false}" })
            Assert.Equal(GraphicsReadbackOutcome.Unavailable, ScreenPrivacy.ReadResult(json).Outcome);
        foreach (var key in new[] {"screenApisAvailable","width","height","availWidth","availHeight","devicePixelRatio","deviceWidthMatches","deviceHeightMatches","resolutionMatches"})
        {
            var observation = Matching(); observation.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable, ScreenPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome);
            observation[key] = null;
            Assert.Equal(GraphicsReadbackOutcome.Unavailable, ScreenPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome);
        }
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, ScreenPrivacy.ReadResult(JsonSerializer.Serialize(Matching()), double.NaN).Outcome);
    }

    [Theory]
    [InlineData("devicePixelRatio", 0.8)]
    [InlineData("devicePixelRatio", 2)]
    [InlineData("devicePixelRatio", 1.25)]
    public void Non_normalized_DPR_is_rejected(string key, double value)
    {
        var observation = Matching(); observation[key] = value;
        Assert.Equal(GraphicsReadbackOutcome.Violation, ScreenPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome);
    }

    [Fact]
    public void CSS_disagreement_is_a_violation_and_native_screen_dimensions_are_retained()
    {
        foreach (var key in new[] { "deviceWidthMatches", "deviceHeightMatches", "resolutionMatches" })
        {
            var observation = Matching(); observation[key] = false;
            Assert.Equal(GraphicsReadbackOutcome.Violation, ScreenPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome);
        }
        var rotated = Matching(); rotated["orientationType"] = "portrait-primary"; rotated["width"] = 2048; rotated["height"] = 1152;
        Assert.Equal(GraphicsReadbackOutcome.Verified, ScreenPrivacy.ReadResult(JsonSerializer.Serialize(rotated)).Outcome);
    }

    [Fact]
    public void Browser_flag_preserves_other_privacy_arguments()
    {
        Assert.Contains("collectScreenObservation", ScreenPrivacy.EvaluationScript);
        ProxyEndpoint.TryParse("http://proxy.test:3128", out var proxy, out _);
        var prior = BrowserArguments.Build(proxy, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental, GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental);
        var current = BrowserArguments.Build(proxy, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental, GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental);
        Assert.Contains(BrowserArguments.DisplayScaleFlag, current.Split(' '));
        Assert.Equal(prior.Split(' '), current.Split(' ').Where(flag => flag != BrowserArguments.DisplayScaleFlag));
        Assert.DoesNotContain(BrowserArguments.DisplayScaleFlag, BrowserArguments.Build(null).Split(' '));
    }

    [Fact]
    public async Task Dpr_mode_and_its_zoom_restart_only_the_edited_profile_with_a_new_snapshot()
    {
        using var env = new TestEnv();
        env.Engine.Capabilities = env.Engine.Capabilities with { GraphicsRestrictionSupported = true };
        var a = env.AddProfile(change: p => p with {GraphicsPolicy = GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental});
        var b = env.AddProfile("B");
        var lifecycle = env.Lifecycle();
        Assert.Equal(OpenOutcome.Opened, (await lifecycle.OpenAsync(a.Id)).Outcome);
        await lifecycle.OpenAsync(b.Id);
        var edited = a with { ZoomFactor = 1.25 };
        Assert.True(env.Catalog.SaveSettings(edited, profileIsLive:true).RestartRequired);
        Assert.Equal(1, env.Engine.Requests[0].Config.ZoomFactor);
        await lifecycle.RestartAsync(a.Id);
        Assert.Equal(1.25, env.Engine.Requests[^1].Config.ZoomFactor);
        Assert.Equal(0, env.Engine.Sessions[1].CloseCalls);
        Assert.Equal(edited.GraphicsPolicy, env.Repository.Get(a.Id)!.GraphicsPolicy);
        Assert.False(ProfileConfig.RequiresRestart(b, b with {ZoomFactor=1.25}));
    }
}
