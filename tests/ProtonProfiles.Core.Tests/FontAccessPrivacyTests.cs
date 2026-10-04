using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class FontAccessPrivacyTests
{
    private const GraphicsPolicy Mode = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental;
    private static Dictionary<string, object?> Absent() => new() {
        ["status"]="Observed", ["secureContext"]=true, ["documentContext"]=true,
        ["queryLocalFontsAvailable"]=false, ["fontDataAvailable"]=false,
    };

    [Fact]
    public void New_mode_retains_previous_protections_proxy_and_native_user_agent_requirement()
    {
        foreach (var policy in Enum.GetValues<GraphicsPolicy>()) Assert.Equal(policy is Mode or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental, FontAccessPrivacy.IsEnabled(policy));
        ProxyEndpoint.TryParse("http://proxy.test:3128", out var proxy, out _);
        var previous=BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental);
        var current=BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,Mode);
        Assert.Equal(previous.Replace(",SharedWorker",",SharedWorker,FontAccess"),current);
        Assert.True(AudioPageGuard.IsEnabled(Mode)); Assert.True(ScreenPrivacy.IsEnabled(Mode));
        Assert.True(SpeechPrivacy.IsEnabled(Mode)); Assert.True(UserAgentHintsPrivacy.IsEnabled(Mode));
        using var env = new TestEnv();
        var profile=env.AddProfile();var edited=profile with {GraphicsPolicy=Mode};
        Assert.True(env.Catalog.SaveSettings(edited,profileIsLive:true).RestartRequired);
        Assert.Equal(Mode,env.Repository.Get(profile.Id)!.GraphicsPolicy);
        Assert.Contains(UserAgentHintsPrivacy.CustomUserAgentError,ProfileValidator.Validate(edited with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="Chosen/1.0"}));
    }

    [Fact]
    public void Confirmation_requires_complete_secure_document_evidence()
    {
        Assert.Equal(GraphicsReadbackOutcome.Verified,FontAccessPrivacy.ReadResult(JsonSerializer.Serialize(Absent())).Outcome);
        foreach (var key in Absent().Keys)
        {
            var partial=Absent();partial.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,FontAccessPrivacy.ReadResult(JsonSerializer.Serialize(partial)).Outcome);
        }
        foreach (var key in new[]{"secureContext","documentContext"})
        {
            var naturalAbsence=Absent();naturalAbsence[key]=false;
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,FontAccessPrivacy.ReadResult(JsonSerializer.Serialize(naturalAbsence)).Outcome);
        }
        foreach (var key in new[]{"queryLocalFontsAvailable","fontDataAvailable"})
        {
            var exposed=Absent();exposed[key]=true;
            Assert.Equal(GraphicsReadbackOutcome.Violation,FontAccessPrivacy.ReadResult(JsonSerializer.Serialize(exposed)).Outcome);
        }
    }

    [Theory]
    [InlineData(null)] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{broken")]
    public void Missing_evidence_does_not_prove_blocking(string? json) =>
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,FontAccessPrivacy.ReadResult(json).Outcome);
}
