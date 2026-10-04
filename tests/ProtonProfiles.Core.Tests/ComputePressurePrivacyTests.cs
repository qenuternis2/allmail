using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;
public class ComputePressurePrivacyTests
{
    private const GraphicsPolicy Mode = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental;
    private static Dictionary<string,object?> Absent(bool worker = false) => new() {
        ["status"]="Observed", ["secureContext"]=true, ["documentContext"]=!worker,
        ["observerAvailable"]=false, ["recordAvailable"]=false
    };
    [Fact]
    public void Pressure_mode_preserves_previous_restrictions_and_requires_restart_and_native_UA()
    {
        foreach (var policy in Enum.GetValues<GraphicsPolicy>()) Assert.Equal(policy==Mode,ComputePressurePrivacy.IsEnabled(policy));
        ProxyEndpoint.TryParse("http://proxy.test:3128",out var proxy,out _);
        var before=BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental);
        var after=BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,Mode);
        Assert.Equal(before.Replace(",WebUSB,WebHID,Serial",",WebUSB,WebHID,Serial,ComputePressure",StringComparison.Ordinal),after);
        Assert.Equal(1,after.Split("--disable-blink-features=").Length-1);
        Assert.True(HardwareDevicesPrivacy.IsEnabled(Mode));Assert.True(HardwareConcurrencyPrivacy.IsEnabled(Mode));
        Assert.True(FontAccessPrivacy.IsEnabled(Mode));Assert.True(UserAgentHintsPrivacy.IsEnabled(Mode));
        Assert.True(AudioPageGuard.IsEnabled(Mode));Assert.True(ScreenPrivacy.IsEnabled(Mode));
        using var env=new TestEnv();var original=env.AddProfile();var edited=original with {GraphicsPolicy=Mode};
        Assert.True(env.Catalog.SaveSettings(edited,profileIsLive:true).RestartRequired);
        Assert.Equal(Mode,env.Repository.Get(original.Id)!.GraphicsPolicy);
        Assert.Contains(UserAgentHintsPrivacy.CustomUserAgentError,ProfileValidator.Validate(edited with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="Chosen/1.0"}));
    }
    [Fact]
    public void Only_complete_absence_in_the_correct_secure_context_confirms_restriction()
    {
        foreach (var worker in new[]{false,true})
        {
            var json=JsonSerializer.Serialize(Absent(worker));
            Assert.Equal(GraphicsReadbackOutcome.Verified,ComputePressurePrivacy.ReadResult(json,worker).Outcome);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,ComputePressurePrivacy.ReadResult(json,!worker).Outcome);
        }
        foreach (var key in Absent().Keys)
        {
            var partial=Absent();partial.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,ComputePressurePrivacy.ReadResult(JsonSerializer.Serialize(partial)).Outcome);
        }
        foreach (var key in new[]{"observerAvailable","recordAvailable"})
        {
            var exposed=Absent();exposed[key]=true;
            Assert.Equal(GraphicsReadbackOutcome.Violation,ComputePressurePrivacy.ReadResult(JsonSerializer.Serialize(exposed)).Outcome);
            exposed[key]="false";
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,ComputePressurePrivacy.ReadResult(JsonSerializer.Serialize(exposed)).Outcome);
            // Known exposure still fails if the other field is missing.
            Assert.Equal(GraphicsReadbackOutcome.Violation,ComputePressurePrivacy.ReadResult(JsonSerializer.Serialize(new {status="Observed",secureContext=true,documentContext=true,observerAvailable=true})).Outcome);
        }
        var insecure=Absent();insecure["secureContext"]=false;
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,ComputePressurePrivacy.ReadResult(JsonSerializer.Serialize(insecure)).Outcome);
    }
    [Theory]
    [InlineData(null)] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{broken")]
    public void Missing_or_malformed_evidence_is_unavailable(string? json) =>
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,ComputePressurePrivacy.ReadResult(json).Outcome);
}
