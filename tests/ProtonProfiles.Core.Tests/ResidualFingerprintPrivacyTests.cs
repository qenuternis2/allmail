using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;
public class ResidualFingerprintPrivacyTests
{
    [Fact]
    public void Only_strict_mode_installs_observable_script_restrictions()
    {
        foreach(var policy in Enum.GetValues<GraphicsPolicy>())Assert.Equal(policy==GraphicsPolicy.StrictFingerprintExperimental,ResidualFingerprintPrivacy.IsEnabled(policy));
        Assert.Contains("deviceMemory",ResidualFingerprintPrivacy.Script);
        Assert.Contains("collectResidualFingerprintObservation",ResidualFingerprintPrivacy.EvaluationScript);
    }
    [Theory]
    [InlineData(null)] [InlineData("{}")] [InlineData("null")] [InlineData("[]")] [InlineData("{broken")]
    public void Invalid_observations_never_verify(string? json)=>Assert.Equal(GraphicsReadbackOutcome.Unavailable,ResidualFingerprintPrivacy.ReadResult(json).Outcome);
    [Theory]
    [InlineData("http://proxy.test:3128","proxy.test")]
    [InlineData("http://[::1]:3128","::1")]
    public void Strict_proxy_removes_bypass_and_local_target_dns(string endpoint,string host)
    {
        Assert.True(ProxyEndpoint.TryParse(endpoint,out var proxy,out _));
        var flags=BrowserArguments.Build(proxy,graphics:GraphicsPolicy.StrictFingerprintExperimental);
        Assert.Contains("--proxy-bypass-list=<-loopback>",flags);Assert.Contains("--disable-quic",flags);
        Assert.Contains("--host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE "+host+"\"",flags);
        Assert.Equal(1,flags.Split(BrowserArguments.WebRtcPolicyFlag).Length-1);
        Assert.DoesNotContain("--host-resolver-rules",BrowserArguments.Build(null,graphics:GraphicsPolicy.StrictFingerprintExperimental));
        Assert.DoesNotContain("--proxy-bypass-list",BrowserArguments.Build(proxy));
    }
}
