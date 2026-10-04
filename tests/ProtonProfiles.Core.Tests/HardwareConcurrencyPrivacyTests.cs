using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class HardwareConcurrencyPrivacyTests
{
    private const GraphicsPolicy Mode = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental;
    private static Dictionary<string,object?> Native() => new() {
        ["status"]="Observed", ["hardwareConcurrency"]=8, ["nativeGetter"]=true, ["ownProperty"]=false,
    };
    [Theory]
    [InlineData(1,1)] [InlineData(2,2)] [InlineData(3,2)] [InlineData(4,4)] [InlineData(6,4)]
    [InlineData(7,4)] [InlineData(8,8)] [InlineData(12,8)] [InlineData(13,8)] [InlineData(int.MaxValue,8)]
    public void Count_rounds_down_and_never_increases(int count,int expected)
    {
        Assert.Equal(expected,HardwareConcurrencyPrivacy.Normalize(count));
        Assert.Equal(expected,HardwareConcurrencyPrivacy.Normalize(expected));
        Assert.True(expected <= count);
    }
    [Fact]
    public void Invalid_counts_are_rejected_and_missing_CDP_evidence_has_no_default()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>HardwareConcurrencyPrivacy.Normalize(0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>HardwareConcurrencyPrivacy.Normalize(-1));
        Assert.Equal(12,HardwareConcurrencyPrivacy.ReadNativeCdpCount("{\"result\":{\"type\":\"number\",\"value\":12}}"));
        foreach(var json in new[]{"null","[]","{broken","{}","{\"result\":{\"type\":\"number\",\"value\":0}}",
            "{\"result\":{\"type\":\"string\",\"value\":\"12\"}}","{\"result\":{\"type\":\"number\",\"value\":1.5}}",
            "{\"result\":{\"type\":\"number\",\"value\":12},\"exceptionDetails\":{}}"})
            Assert.Throws<InvalidOperationException>(()=>HardwareConcurrencyPrivacy.ReadNativeCdpCount(json));
    }
    [Fact]
    public void CPU_mode_retains_all_previous_arguments_and_requires_restart_and_native_UA()
    {
        foreach(var policy in Enum.GetValues<GraphicsPolicy>()) Assert.Equal(policy is Mode or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental,HardwareConcurrencyPrivacy.IsEnabled(policy));
        ProxyEndpoint.TryParse("http://proxy.test:3128",out var proxy,out _);
        Assert.Equal(BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental),
            BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,Mode));
        Assert.True(AudioPageGuard.IsEnabled(Mode));Assert.True(ScreenPrivacy.IsEnabled(Mode));
        Assert.True(SpeechPrivacy.IsEnabled(Mode));Assert.True(FontAccessPrivacy.IsEnabled(Mode));Assert.True(UserAgentHintsPrivacy.IsEnabled(Mode));
        using var env=new TestEnv();var original=env.AddProfile();var edited=original with {GraphicsPolicy=Mode};
        Assert.True(env.Catalog.SaveSettings(edited,profileIsLive:true).RestartRequired);
        Assert.Equal(Mode,env.Repository.Get(original.Id)!.GraphicsPolicy);
        Assert.Contains(UserAgentHintsPrivacy.CustomUserAgentError,ProfileValidator.Validate(edited with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="Chosen/1.0"}));
    }
    [Fact]
    public void Only_matching_count_and_unmodified_native_getter_confirm_normalization()
    {
        Assert.Equal(GraphicsReadbackOutcome.Verified,HardwareConcurrencyPrivacy.ReadResult(JsonSerializer.Serialize(Native()),8).Outcome);
        foreach(var key in Native().Keys)
        {
            var partial=Native();partial.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareConcurrencyPrivacy.ReadResult(JsonSerializer.Serialize(partial),8).Outcome);
        }
        foreach(var pair in new Dictionary<string,object>{{"hardwareConcurrency",12},{"nativeGetter",false},{"ownProperty",true}})
        {
            var violation=Native();violation[pair.Key]=pair.Value;
            Assert.Equal(GraphicsReadbackOutcome.Violation,HardwareConcurrencyPrivacy.ReadResult(JsonSerializer.Serialize(violation),8).Outcome);
        }
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareConcurrencyPrivacy.ReadResult(JsonSerializer.Serialize(Native()),null).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareConcurrencyPrivacy.ReadResult(JsonSerializer.Serialize(Native()),12).Outcome);
    }
    [Theory]
    [InlineData(null)] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{broken")]
    public void Missing_observations_never_confirm_normalization(string? json) =>
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareConcurrencyPrivacy.ReadResult(json,8).Outcome);
}
