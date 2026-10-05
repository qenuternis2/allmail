using System.Text.Json;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class StandardFingerprintPrivacyTests
{
    [Theory]
    [InlineData("Sec-CH-Device-Memory",true)] [InlineData("sEc-Ch-Prefers-Reduced-Motion",true)]
    [InlineData("device-memory",true)] [InlineData("RTT",true)] [InlineData("Viewport-Width",true)]
    [InlineData("Authorization",false)] [InlineData("Proxy-Authorization",false)] [InlineData("Cookie",false)]
    [InlineData("Accept-Language",false)] [InlineData("Origin",false)] [InlineData("X-Sec-CH-Device-Memory",false)]
    public void Remove_only_client_hint_headers(string name,bool expected) => Assert.Equal(expected,StandardFingerprintPrivacy.IsClientHintHeader(name));
    private static Dictionary<string,object?> DisplayObservation()=>new() {
        ["status"]="Observed",["media"]=new Dictionary<string,object?>{["continuous"]=true,["folded"]=false,["horizontalSingle"]=false,["horizontalDouble"]=false,["verticalSingle"]=false,["verticalDouble"]=false},
        ["css"]=new Dictionary<string,object?>{["posture"]="continuous",["horizontal"]=0,["vertical"]=0,["envSupported"]=true,["secondSegmentLeft"]=777},
        ["postureApiAvailable"]=true,["postureType"]="continuous",["nativePostureGetter"]=true,["viewportApiAvailable"]=false,["segmentCount"]=null,["nativeSegmentsGetter"]=null
    };
    private static Dictionary<string, object?> Observation() => new()
    {
        ["status"]="Observed", ["documentContext"]=true,["displayPosture"]=DisplayObservation(),
        ["media"]=StandardFingerprintPrivacy.MediaFeatures.Keys.ToDictionary(k=>k,k=>(object?)true),
        ["genericFonts"]=new Dictionary<string, object?> { ["serif"]=true,["sansSerif"]=true,["fixed"]=true,["cursive"]=true,["fantasy"]=true,["math"]=true },
        ["localFontLoad"]=true,["localFontRendering"]=false,["defaultFontSize"]=16,["osTextScale"]=1
    };
    private static GraphicsReadbackOutcome Outcome(Dictionary<string,object?> observation) => StandardFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome;
    [Fact]
    public void Script_blocked_constructor_does_not_skip_media_and_font_defaults_verification()
    {
        var o=Observation();o["localFontConstructionBlocked"]=true;o["localFontLoad"]=null;o["localFontRendering"]=null;
        Assert.Equal(GraphicsReadbackOutcome.Verified,Outcome(o));
        o["defaultFontSize"]=20;Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(o));
        o["defaultFontSize"]=16;o["osTextScale"]=2;Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(o));
        o["osTextScale"]=1;((Dictionary<string,object?>)o["media"]!)["color-gamut"]=false;
        Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(o));
    }
    [Fact]
    public void Real_readback_is_required_for_each_preference_and_font()
    {
        Assert.Equal(GraphicsReadbackOutcome.Verified,Outcome(Observation()));
        foreach(var group in new[]{"media","genericFonts"})
            foreach(var key in ((Dictionary<string,object?>)Observation()[group]!).Keys)
            {
                var partial=Observation(); ((Dictionary<string,object?>)partial[group]!).Remove(key);
                Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(partial));
                var altered=Observation(); ((Dictionary<string,object?>)altered[group]!)[key]=false;
                Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(altered));
            }
        var local=Observation();local["localFontRendering"]=true;
        Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(local));
        local["localFontRendering"]=null;
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(local));
        local=Observation();local["documentContext"]=false;
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(local));
        foreach(var key in new[]{"defaultFontSize","osTextScale"})
        {
            var altered=Observation();altered[key]=2;
            Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(altered));
            altered.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(altered));
        }
    }
    [Fact]
    public void Unsupported_optional_queries_are_explicit_null_not_claimed_supported()
    {
        var observation=Observation(); var media=(Dictionary<string,object?>)observation["media"]!;
        media["prefers-reduced-data"]=null;media["prefers-reduced-transparency"]=null;
        Assert.Equal(GraphicsReadbackOutcome.Verified,Outcome(observation));
        media["color-gamut"]=null;
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(observation));
    }
    [Theory]
    [InlineData("continuous")] [InlineData("folded")] [InlineData("horizontalSingle")] [InlineData("horizontalDouble")] [InlineData("verticalSingle")] [InlineData("verticalDouble")]
    public void Posture_media_requires_complete_actual_results(string key)
    {
        var o=Observation();var display=(Dictionary<string,object?>)o["displayPosture"]!;var media=(Dictionary<string,object?>)display["media"]!;
        media[key]=!(bool)media[key]!;Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(o));
        media[key]=null;Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(o));
        media.Remove(key);Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(o));
    }
    [Theory]
    [InlineData("posture", "folded")] [InlineData("horizontal",2)] [InlineData("vertical",2)] [InlineData("secondSegmentLeft",188)]
    public void Posture_actual_CSS_and_env_cannot_disagree_with_media(string key,object value)
    {
        var o=Observation();var display=(Dictionary<string,object?>)o["displayPosture"]!;var css=(Dictionary<string,object?>)display["css"]!;
        css[key]=value;Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(o));
        css.Remove(key);Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(o));
    }
    [Fact]
    public void Posture_API_absence_is_explicit_and_missing_old_report_never_passes()
    {
        var o=Observation();o.Remove("displayPosture");Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(o));
        var display=DisplayObservation();o["displayPosture"]=display;
        display["postureType"]="folded";Assert.Equal(GraphicsReadbackOutcome.Violation,Outcome(o));
        display["postureApiAvailable"]=false;display["postureType"]=null;display["nativePostureGetter"]=null;
        display["viewportApiAvailable"]=false;display["segmentCount"]=null;display["nativeSegmentsGetter"]=null;Assert.Equal(GraphicsReadbackOutcome.Verified,Outcome(o));
        display["status"]="NotApplicable";Assert.Equal(GraphicsReadbackOutcome.Unavailable,Outcome(o));
    }
    [Fact]
    public void Real_screen_exception_relaxes_only_display_readback_and_restores_native_flags()
    {
        var o=Observation();o.Remove("displayPosture");var json=JsonSerializer.Serialize(o);
        Assert.Equal(GraphicsReadbackOutcome.Verified,StandardFingerprintPrivacy.ReadResult(json,allowScreenWorkArea:true).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,StandardFingerprintPrivacy.ReadResult(json,allowLocalFonts:true).Outcome);
        o["defaultFontSize"]=20;Assert.Equal(GraphicsReadbackOutcome.Violation,StandardFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o),allowScreenWorkArea:true).Outcome);
        Assert.Contains("ViewportSegments",ProtonProfiles.Core.Network.BrowserArguments.Build(null,graphics:ProtonProfiles.Core.Model.GraphicsPolicy.StrictFingerprintExperimental));
        Assert.DoesNotContain("ViewportSegments",ProtonProfiles.Core.Network.BrowserArguments.Build(null,graphics:ProtonProfiles.Core.Model.GraphicsPolicy.StrictFingerprintExperimental,exceptions:ProtonProfiles.Core.Model.PrivacyException.ScreenWorkArea));
        Assert.Contains(StandardFingerprintPrivacy.Commands(),command=>command.Method=="Emulation.setDevicePostureOverride");
        Assert.DoesNotContain(StandardFingerprintPrivacy.Commands(allowScreenWorkArea:true),command=>command.Method=="Emulation.setDevicePostureOverride");
    }
    [Theory]
    [InlineData(null)] [InlineData("{}")] [InlineData("null")] [InlineData("[]")] [InlineData("{broken")]
    public void Errors_never_confirm_defaults(string? json) => Assert.Equal(GraphicsReadbackOutcome.Unavailable,StandardFingerprintPrivacy.ReadResult(json).Outcome);
}
