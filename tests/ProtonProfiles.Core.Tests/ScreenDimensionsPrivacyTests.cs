using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class ScreenDimensionsPrivacyTests
{
    private const GraphicsPolicy Mode = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndScreenExperimental;
    private static Dictionary<string,object?> Matching() => new() {
        ["screenApisAvailable"] = true, ["width"] = 1920, ["height"] = 1080,
        ["availWidth"] = 1920, ["availHeight"] = 1080, ["availLeft"] = 0, ["availTop"] = 0,
        ["orientationType"] = "landscape-primary", ["orientationAngle"] = 0,
        ["deviceWidthMatches"] = true, ["deviceHeightMatches"] = true,
        ["nativeGetters"] = true, ["ownProperties"] = false
    };
    [Fact]
    public void Screen_mode_preserves_previous_arguments_and_persists_with_restart_and_native_UA()
    {
        foreach (var policy in Enum.GetValues<GraphicsPolicy>()) Assert.Equal(policy == Mode, ScreenDimensionsPrivacy.IsEnabled(policy));
        ProxyEndpoint.TryParse("http://proxy.test:3128",out var proxy,out _);
        Assert.Equal(BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental),
            BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,Mode));
        Assert.True(HardwareConcurrencyPrivacy.IsEnabled(Mode)); Assert.True(ScreenPrivacy.IsEnabled(Mode));
        Assert.True(FontAccessPrivacy.IsEnabled(Mode)); Assert.True(UserAgentHintsPrivacy.IsEnabled(Mode));
        using var env = new TestEnv(); var original = env.AddProfile();
        var edited = original with {GraphicsPolicy = Mode};
        Assert.True(env.Catalog.SaveSettings(edited,profileIsLive:true).RestartRequired);
        Assert.Equal(Mode,env.Repository.Get(original.Id)!.GraphicsPolicy);
        Assert.Contains(UserAgentHintsPrivacy.CustomUserAgentError,ProfileValidator.Validate(edited with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="Chosen/1.0"}));
    }
    [Fact]
    public void Partial_or_malformed_screen_evidence_never_confirms_normalization()
    {
        Assert.Equal(GraphicsReadbackOutcome.Verified,ScreenDimensionsPrivacy.ReadResult(JsonSerializer.Serialize(Matching())).Outcome);
        foreach (var key in Matching().Keys)
        {
            var partial = Matching(); partial.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,ScreenDimensionsPrivacy.ReadResult(JsonSerializer.Serialize(partial)).Outcome);
        }
        foreach (var json in new string?[] {null,"null","[]","{broken","{}","{\"screenApisAvailable\":false}"})
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,ScreenDimensionsPrivacy.ReadResult(json).Outcome);
        var malformed = Matching(); malformed["width"] = "1920";
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,ScreenDimensionsPrivacy.ReadResult(JsonSerializer.Serialize(malformed)).Outcome);
    }
    [Theory]
    [InlineData("width",2560)] [InlineData("height",1440)] [InlineData("availHeight",1040)]
    [InlineData("availLeft",1920)] [InlineData("availTop",20)] [InlineData("orientationAngle",90)]
    [InlineData("orientationType","portrait-primary")] [InlineData("nativeGetters",false)]
    [InlineData("ownProperties",true)] [InlineData("deviceWidthMatches",false)] [InlineData("deviceHeightMatches",false)]
    public void Exposed_dimensions_or_JS_replacements_fail_verification(string key,object value)
    {
        var exposed = Matching(); exposed[key] = value;
        Assert.Equal(GraphicsReadbackOutcome.Violation,ScreenDimensionsPrivacy.ReadResult(JsonSerializer.Serialize(exposed)).Outcome);
    }
}
