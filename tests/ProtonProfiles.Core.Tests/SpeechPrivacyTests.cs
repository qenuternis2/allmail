using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class SpeechPrivacyTests
{
    private static Dictionary<string, object?> Absent() => new() {
        ["synthesisAvailable"] = false, ["synthesisConstructorAvailable"] = false,
        ["utteranceConstructorAvailable"] = false, ["voiceConstructorAvailable"] = false,
    };

    [Fact]
    public void Native_restriction_is_opt_in_and_preserves_proxy_and_previous_privacy_arguments()
    {
        foreach (var policy in Enum.GetValues<GraphicsPolicy>())
            Assert.Equal(policy is GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental, SpeechPrivacy.IsEnabled(policy));
        ProxyEndpoint.TryParse("http://proxy.test:3128", out var proxy, out _);
        var prior = BrowserArguments.Build(proxy, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental, GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental);
        var current = BrowserArguments.Build(proxy, WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental, GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental);
        Assert.Contains(SpeechPrivacy.BrowserFlag, current.Split(' '));
        Assert.Equal(prior.Split(' '), current.Split(' ').Where(flag => flag != SpeechPrivacy.BrowserFlag));
        Assert.DoesNotContain(SpeechPrivacy.BrowserFlag, BrowserArguments.Build(null).Split(' '));
        Assert.Contains("collectSpeechObservation", SpeechPrivacy.EvaluationScript);
        Assert.True(AudioPageGuard.IsEnabled(GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental));
        Assert.True(ScreenPrivacy.IsEnabled(GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental));
    }

    [Fact]
    public void All_four_explicitly_absent_entry_points_are_required()
    {
        Assert.Equal(GraphicsReadbackOutcome.Verified, SpeechPrivacy.ReadResult(JsonSerializer.Serialize(Absent())).Outcome);
        foreach (var key in Absent().Keys)
        {
            var observation = Absent(); observation.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable, SpeechPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome);
            observation[key] = true;
            Assert.Equal(GraphicsReadbackOutcome.Violation, SpeechPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome);
        }
        // A known available entry point is actionable even when other evidence is missing.
        Assert.Equal(GraphicsReadbackOutcome.Violation, SpeechPrivacy.ReadResult("{\"synthesisAvailable\":true}").Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("{\"synthesisAvailable\":\"false\"}")]
    public void Invalid_and_missing_evidence_never_confirms_blocking(string? json) =>
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, SpeechPrivacy.ReadResult(json).Outcome);

    [Fact]
    public void New_mode_and_its_zoom_require_restart_while_existing_modes_are_unchanged()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        var edited = a with {GraphicsPolicy=GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental};
        Assert.True(env.Catalog.SaveSettings(edited, profileIsLive:true).RestartRequired);
        Assert.Equal(edited.GraphicsPolicy, env.Repository.Get(a.Id)!.GraphicsPolicy);
        Assert.True(ProfileConfig.RequiresRestart(edited, edited with {ZoomFactor=1.25}));
        Assert.False(ProfileConfig.RequiresRestart(a, a with {ZoomFactor=1.25}));
    }
}
