using System.Text.Json;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class UserAgentHintsPrivacyTests
{
    private const GraphicsPolicy Mode = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental;
    private const string Ua = "Mozilla/5.0 Chrome/154.0.0.0 Safari/537.36 Edg/154.0.0.0";
    private static Dictionary<string, object?> Empty() => new() {
        ["status"]="Observed", ["secureContext"]=true, ["userAgent"]=Ua, ["sharedWorkerAvailable"]=false, ["uaDataAvailable"]=true,
        ["lowEntropy"]=new Dictionary<string,object?> { ["brands"]=Array.Empty<object>(),["platform"]="",["mobile"]=false },
        ["highEntropy"]=new Dictionary<string,object?> { ["architecture"]="",["bitness"]="",["model"]="",["platformVersion"]="",["uaFullVersion"]="",["fullVersionList"]=Array.Empty<object>(),["formFactors"]=Array.Empty<object>(),["wow64"]=false },
    };
    private static GraphicsReadbackOutcome Read(Dictionary<string,object?> v, string? expected = Ua) => UserAgentHintsPrivacy.ReadResult(JsonSerializer.Serialize(v),expected).Outcome;

    [Fact]
    public void Opt_in_preserves_effective_UA_and_previous_flags()
    {
        foreach (var mode in Enum.GetValues<GraphicsPolicy>()) Assert.Equal(mode is Mode or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental, UserAgentHintsPrivacy.IsEnabled(mode));
        var p=new ProfileConfig {Id=Guid.NewGuid(),DisplayName="test",GraphicsPolicy=Mode};
        Assert.Equal(Ua, UserAgentHintsPrivacy.UserAgentToApply(p, Ua));
        var custom = p with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="Chosen/1.0"};
        Assert.Equal(UserAgentHintsPrivacy.CustomUserAgentError, Assert.Throws<ArgumentException>(() => UserAgentHintsPrivacy.UserAgentToApply(custom, Ua)).Message);
        Assert.Contains(UserAgentHintsPrivacy.CustomUserAgentError, ProfileValidator.Validate(custom));
        var legacyCustom = custom with {GraphicsPolicy=GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental};
        Assert.DoesNotContain(UserAgentHintsPrivacy.CustomUserAgentError, ProfileValidator.Validate(legacyCustom));
        Assert.Equal("Chosen/1.0",UserAgentHintsPrivacy.UserAgentToApply(legacyCustom,Ua));
        Assert.Throws<ArgumentException>(()=>UserAgentHintsPrivacy.UserAgentToApply(p, ""));
        Assert.Throws<ArgumentException>(()=>UserAgentHintsPrivacy.UserAgentToApply(p with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="bad\r\nUA"}, Ua));
        ProxyEndpoint.TryParse("http://proxy.test:3128", out var proxy,out _);
        Assert.Equal(BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental).Replace(SpeechPrivacy.BrowserFlag,SpeechPrivacy.BrowserFlag+",SharedWorker"),
            BrowserArguments.Build(proxy,WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental,Mode));
        Assert.True(AudioPageGuard.IsEnabled(Mode));Assert.True(ScreenPrivacy.IsEnabled(Mode));Assert.True(SpeechPrivacy.IsEnabled(Mode));
        Assert.Contains("getHighEntropyValues",UserAgentHintsPrivacy.EvaluationScript);
    }
    [Fact]
    public void Empty_or_absent_native_UA_data_requires_matching_UA_and_successful_observation()
    {
        Assert.Equal(GraphicsReadbackOutcome.Verified, Read(Empty()));
        Assert.Equal(GraphicsReadbackOutcome.Violation, Read(Empty(),"different"));
        var v=Empty();v["uaDataAvailable"]=false;v.Remove("lowEntropy");v.Remove("highEntropy");
        Assert.Equal(GraphicsReadbackOutcome.Verified,Read(v));
        v.Remove("status");Assert.Equal(GraphicsReadbackOutcome.Unavailable,Read(v));
        foreach (var key in new[]{"secureContext","lowEntropy","highEntropy","userAgent","uaDataAvailable","sharedWorkerAvailable"}) {
            v=Empty();v.Remove(key);Assert.Equal(GraphicsReadbackOutcome.Unavailable,Read(v));
        }
        v=Empty();v["secureContext"]=false;Assert.Equal(GraphicsReadbackOutcome.Unavailable,Read(v));
        v=Empty();v["sharedWorkerAvailable"]=true;Assert.Equal(GraphicsReadbackOutcome.Violation,Read(v));
    }
    [Fact]
    public void Remaining_high_entropy_data_and_low_entropy_identity_are_violations()
    {
        foreach (var key in new[]{"architecture","bitness","model","platformVersion","uaFullVersion","platform"}) {
            var v=Empty();((Dictionary<string,object?>)v["highEntropy"]!)[key]="native-value";
            Assert.Equal(GraphicsReadbackOutcome.Violation,Read(v));
            ((Dictionary<string,object?>)v["highEntropy"]!)[key]=123;Assert.Equal(GraphicsReadbackOutcome.Unavailable,Read(v));
        }
        foreach (var key in new[]{"brands","fullVersionList","formFactors"}) {
            var v=Empty();((Dictionary<string,object?>)v["highEntropy"]!)[key]=new[]{"native-value"};Assert.Equal(GraphicsReadbackOutcome.Violation,Read(v));
        }
        var low=Empty();((Dictionary<string,object?>)low["lowEntropy"]!)["brands"]=new[]{new{brand="Edge",version="154"}};
        Assert.Equal(GraphicsReadbackOutcome.Violation,Read(low));
    }
    [Theory]
    [InlineData(null)] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{broken")]
    [InlineData("{\"status\":123}")] [InlineData("{\"status\":\"NotPerformed\"}")]
    public void Invalid_evidence_never_confirms_suppression(string? json) => Assert.Equal(GraphicsReadbackOutcome.Unavailable,UserAgentHintsPrivacy.ReadResult(json,Ua).Outcome);
    [Fact]
    public void Cdp_exception_and_missing_value_do_not_confirm_suppression()
    {
        var result=JsonSerializer.Serialize(new{result=new{value=Empty()}});
        Assert.Equal(GraphicsReadbackOutcome.Verified, UserAgentHintsPrivacy.ReadCdpResult(result,Ua).Outcome);
        result=JsonSerializer.Serialize(new{result=new{value=Empty()},exceptionDetails=new{text="failure"}});
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, UserAgentHintsPrivacy.ReadCdpResult(result,Ua).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, UserAgentHintsPrivacy.ReadCdpResult("{\"result\":{}}",Ua).Outcome);
    }
    [Fact]
    public void Mode_persists_requires_restart_and_diagnostics_do_not_claim_runtime_coverage()
    {
        using var env=new TestEnv();var a=env.AddProfile();var p=a with{GraphicsPolicy=Mode};
        Assert.True(env.Catalog.SaveSettings(p,profileIsLive:true).RestartRequired);
        Assert.Equal(Mode,env.Repository.Get(a.Id)!.GraphicsPolicy);
        Assert.True(ProfileConfig.RequiresRestart(p,p with {ZoomFactor=1.25}));
        var lifecycle=env.Lifecycle();
        var text=DiagnosticsReport.Build(new EnvironmentInfo("test","test",null,null,"test","test","test"),env.Engine.Capabilities,[(p,lifecycle.GetState(p.Id))],[]);
        using var report=JsonDocument.Parse(text);var profile=report.RootElement.GetProperty("profiles")[0];
        Assert.True(profile.GetProperty("uaClientHintsRestrictionRequested").GetBoolean());
        Assert.Equal("NotPerformed",profile.GetProperty("uaClientHintsRuntimeCoverage").GetString());
    }
}
