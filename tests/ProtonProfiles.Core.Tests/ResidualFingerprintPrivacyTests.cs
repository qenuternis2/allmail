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

    private static Dictionary<string,object?> Observation() => new()
    {
        ["status"]="Observed",["documentContext"]=true,["deviceMemory"]=8,["scriptRestriction"]=true,
        ["navigatorApis"]=new[]{"getBattery","getGamepads","mediaDevices","mediaCapabilities","serviceWorker"}.ToDictionary(k=>k,_=>false),
        ["constructors"]=new[]{"BatteryManager","Gamepad","GamepadButton","GamepadEvent","GamepadHapticActuator","MediaDevices","MediaDeviceInfo","InputDeviceInfo","MediaCapabilities","Accelerometer","LinearAccelerationSensor","GravitySensor","Gyroscope","AbsoluteOrientationSensor","RelativeOrientationSensor","IdleDetector","ScreenDetails","ScreenDetailed","MemoryInfo","ServiceWorker","ServiceWorkerContainer","ServiceWorkerRegistration"}.ToDictionary(k=>k,_=>false),
        ["webCodecs"]=new[]{"AudioDecoder","VideoDecoder","AudioEncoder","VideoEncoder","AudioData","VideoFrame","EncodedAudioChunk","EncodedVideoChunk"}.ToDictionary(k=>k,_=>false),
        ["canvasTextMetrics"]=new[]{"html","offscreen"}.ToDictionary(k=>k,_=>(object?)false),
        ["keyboardLayout"]=new[]{"keyboard","Keyboard","KeyboardLayoutMap","getLayoutMap","lock","unlock"}.ToDictionary(k=>k,_=>(object?)false),
        ["performanceMemoryAvailable"]=false,["storageEstimateAvailable"]=false,["getScreenDetailsAvailable"]=false,["localFontConstructionBlocked"]=true
    };
    [Fact]
    public void Complete_keyboard_restriction_verifies_and_old_observation_does_not()
    {
        var o=Observation();Assert.Equal(GraphicsReadbackOutcome.Verified,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
        o.Remove("keyboardLayout");Assert.Equal(GraphicsReadbackOutcome.Unavailable,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
    }
    [Theory]
    [InlineData("keyboard")] [InlineData("Keyboard")] [InlineData("KeyboardLayoutMap")]
    [InlineData("getLayoutMap")] [InlineData("lock")] [InlineData("unlock")]
    public void Missing_nonboolean_and_exposed_keyboard_paths_cannot_verify(string key)
    {
        var o=Observation();var apis=(Dictionary<string,object?>)o["keyboardLayout"]!;
        apis.Remove(key);Assert.Equal(GraphicsReadbackOutcome.Unavailable,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
        apis[key]=null;Assert.Equal(GraphicsReadbackOutcome.Unavailable,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
        apis[key]=true;Assert.Equal(GraphicsReadbackOutcome.Violation,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
    }
    [Theory]
    [InlineData("html")] [InlineData("offscreen")]
    public void Canvas_metrics_need_complete_evidence_and_independent_exception(string key)
    {
        var o=Observation();var paths=(Dictionary<string,object?>)o["canvasTextMetrics"]!;
        paths.Remove(key);Assert.Equal(GraphicsReadbackOutcome.Unavailable,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
        paths[key]=null;Assert.Equal(GraphicsReadbackOutcome.Unavailable,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
        paths[key]=true;Assert.Equal(GraphicsReadbackOutcome.Violation,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Verified,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o),PrivacyException.CanvasTextMetrics).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Violation,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o),PrivacyException.CanvasReadback|PrivacyException.LocalFonts).Outcome);
    }
    [Fact]
    public void Same_count_with_wrong_api_name_does_not_verify()
    {
        var o=Observation();var apis=(Dictionary<string,bool>)o["navigatorApis"]!;
        apis.Remove("getBattery");apis["unknownApi"]=false;
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,ResidualFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
    }
}
