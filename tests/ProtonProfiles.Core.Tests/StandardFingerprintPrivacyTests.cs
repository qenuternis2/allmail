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
    private static Dictionary<string, object?> Observation() => new()
    {
        ["status"]="Observed", ["documentContext"]=true,
        ["media"]=StandardFingerprintPrivacy.MediaFeatures.Keys.ToDictionary(k=>k,k=>(object?)true),
        ["genericFonts"]=new Dictionary<string, object?> { ["serif"]=true,["sansSerif"]=true,["fixed"]=true,["cursive"]=true,["fantasy"]=true,["math"]=true },
        ["localFontLoad"]=true,["localFontRendering"]=false,["defaultFontSize"]=16,["osTextScale"]=1
    };
    private static GraphicsReadbackOutcome Outcome(Dictionary<string,object?> observation) => StandardFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(observation)).Outcome;
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
    [InlineData(null)] [InlineData("{}")] [InlineData("null")] [InlineData("[]")] [InlineData("{broken")]
    public void Errors_never_confirm_defaults(string? json) => Assert.Equal(GraphicsReadbackOutcome.Unavailable,StandardFingerprintPrivacy.ReadResult(json).Outcome);
}
