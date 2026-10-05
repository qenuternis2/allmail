using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;
public class AdditionalFingerprintPrivacyTests
{
    private static Dictionary<string,object?> Observation(bool worker=false) => new() {
        ["status"]="Observed",["secureContext"]=true,["documentContext"]=!worker,
        ["apis"]=new[]{"xr","cpuPerformance","measureMemory","getDisplayMedia","selectAudioOutput","AmbientLightSensor","Magnetometer","NDEFReader","NDEFRecord","NDEFMessage","presentation","mediaRemote","RemotePlayback","Presentation","PresentationRequest","PresentationAvailability","PresentationConnection","PresentationConnectionAvailableEvent","PresentationConnectionCloseEvent","PresentationConnectionList","PresentationReceiver"}.ToDictionary(k=>k,k=>(object?)false),
        ["permissions"]=AdditionalFingerprintPrivacy.DeniedPermissions.ToDictionary(k=>k,k=>(object?)"denied"),
        ["connection"]=new Dictionary<string,object?> { ["status"]="Observed",["nativeGetters"]=true,["effectiveType"]="4g",["rtt"]=150,["downlink"]=1.5 }
    };
    [Fact]
    public void Strict_mode_is_cumulative_native_and_persisted_with_restart()
    {
        const GraphicsPolicy mode=GraphicsPolicy.StrictFingerprintExperimental;
        foreach(var value in Enum.GetValues<GraphicsPolicy>()) Assert.Equal(value==mode,AdditionalFingerprintPrivacy.IsEnabled(value));
        var flags=BrowserArguments.Build(null,graphics:mode);
        Assert.Contains(AdditionalFingerprintPrivacy.NetworkFlag,flags);Assert.Contains(AdditionalFingerprintPrivacy.BlinkFeatures,flags);
        Assert.Equal(1,flags.Split("--disable-blink-features=").Length-1);
        Assert.True(ComputePressurePrivacy.IsEnabled(mode));Assert.True(HardwareDevicesPrivacy.IsEnabled(mode));Assert.True(HardwareConcurrencyPrivacy.IsEnabled(mode));
        Assert.True(FontAccessPrivacy.IsEnabled(mode));Assert.True(UserAgentHintsPrivacy.IsEnabled(mode));Assert.True(AudioPageGuard.IsEnabled(mode));Assert.True(ScreenPrivacy.IsEnabled(mode));
        Assert.Contains(BrowserArguments.CanvasReadbackFlag,flags);
        using var env=new TestEnv();var p=env.AddProfile();var edited=p with {GraphicsPolicy=mode};
        Assert.True(env.Catalog.SaveSettings(edited,profileIsLive:true).RestartRequired);Assert.Equal(mode,env.Repository.Get(p.Id)!.GraphicsPolicy);
        Assert.Contains(UserAgentHintsPrivacy.CustomUserAgentError,ProfileValidator.Validate(edited with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="Chosen/1.0"}));
    }
    [Fact]
    public void Privileged_camera_and_MIDI_variants_are_denied_without_origin_scope()
    {
        foreach(var name in AdditionalFingerprintPrivacy.DeniedPermissions)
        {
            using var d=JsonDocument.Parse(AdditionalFingerprintPrivacy.PermissionArguments(name));
            Assert.Equal("denied",d.RootElement.GetProperty("setting").GetString());
            Assert.False(d.RootElement.TryGetProperty("origin",out _));
            if(name=="camera-ptz") Assert.True(d.RootElement.GetProperty("permission").GetProperty("panTiltZoom").GetBoolean());
            if(name=="midi-sysex") Assert.True(d.RootElement.GetProperty("permission").GetProperty("sysex").GetBoolean());
        }
        Assert.Throws<ArgumentOutOfRangeException>(()=>AdditionalFingerprintPrivacy.PermissionArguments("notifications"));
    }
    [Fact]
    public void Absence_denied_permissions_and_native_network_estimates_are_all_required()
    {
        var o=Observation();Assert.Equal(GraphicsReadbackOutcome.Verified,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(o)).Outcome);
        foreach(var group in new[]{"apis","permissions","connection"})
        {
            foreach(var key in ((Dictionary<string,object?>)o[group]!).Keys)
            {
                var partial=Observation();((Dictionary<string,object?>)partial[group]!).Remove(key);
                Assert.Equal(GraphicsReadbackOutcome.Unavailable,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(partial)).Outcome);
            }
        }
        foreach(var key in ((Dictionary<string,object?>)o["apis"]!).Keys)
        {
            var exposed=Observation();((Dictionary<string,object?>)exposed["apis"]!)[key]=true;
            Assert.Equal(GraphicsReadbackOutcome.Violation,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(exposed)).Outcome);
        }
        foreach(var permission in AdditionalFingerprintPrivacy.DeniedPermissions)
        {
            var granted=Observation();((Dictionary<string,object?>)granted["permissions"]!)[permission]="granted";
            Assert.Equal(GraphicsReadbackOutcome.Violation,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(granted)).Outcome);
        }
        var measured=Observation();((Dictionary<string,object?>)measured["connection"]!)["rtt"]=500;
        Assert.Equal(GraphicsReadbackOutcome.Violation,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(measured)).Outcome);
        var patched=Observation();((Dictionary<string,object?>)patched["connection"]!)["nativeGetters"]=false;
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(patched)).Outcome);
        var worker=Observation(true);worker.Remove("permissions");
        Assert.Equal(GraphicsReadbackOutcome.Verified,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(worker),worker:true).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(worker)).Outcome);
    }
    [Theory]
    [InlineData("camera", "prompt")]
    [InlineData("accelerometer", "granted")]
    public void Startup_permission_failure_identifies_the_permission_and_state(string permission, string state)
    {
        var observation = Observation();
        ((Dictionary<string, object?>)observation["permissions"]!)[permission] = state;
        var result = AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(observation));
        Assert.Equal(GraphicsReadbackOutcome.Violation, result.Outcome);
        Assert.Contains(permission, result.Detail);
        Assert.Contains(state, result.Detail);
    }
    [Fact]
    public void Startup_network_failure_identifies_the_native_estimates()
    {
        var observation = Observation();
        ((Dictionary<string, object?>)observation["connection"]!)["rtt"] = 500;
        var result = AdditionalFingerprintPrivacy.ReadResult(JsonSerializer.Serialize(observation));
        Assert.Equal(GraphicsReadbackOutcome.Violation, result.Outcome);
        Assert.Contains("rtt=500, downlink=1.5", result.Detail);
    }
    [Theory]
    [InlineData(null)] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{broken")]
    public void Invalid_observations_never_confirm_restriction(string? json) => Assert.Equal(GraphicsReadbackOutcome.Unavailable,AdditionalFingerprintPrivacy.ReadResult(json).Outcome);

    [Fact]
    public void Prompt_only_allows_startup_with_a_ready_native_request_guard_and_report_stays_truthful()
    {
        var observation = Observation();
        ((Dictionary<string, object?>)observation["permissions"]!)["camera"] = "prompt";
        var json = JsonSerializer.Serialize(observation);
        Assert.Equal(GraphicsReadbackOutcome.Violation, AdditionalFingerprintPrivacy.ReadResult(json).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Violation, AdditionalFingerprintPrivacy.ReadStartupResult(json, false).Outcome);
        Assert.Equal(GraphicsReadbackOutcome.Verified, AdditionalFingerprintPrivacy.ReadStartupResult(json, true).Outcome);
    }

    [Theory]
    [InlineData("camera")] [InlineData("microphone")] [InlineData("geolocation")]
    [InlineData("midi")] [InlineData("camera-ptz")] [InlineData("midi-sysex")]
    [InlineData("idle-detection")] [InlineData("window-management")]
    public void Active_native_guard_never_accepts_sensitive_permission_grants(string permission)
    {
        var observation = Observation();
        ((Dictionary<string, object?>)observation["permissions"]!)[permission] = "granted";
        Assert.Equal(GraphicsReadbackOutcome.Violation, AdditionalFingerprintPrivacy.ReadStartupResult(JsonSerializer.Serialize(observation), true).Outcome);
    }

    [Theory]
    [InlineData(false, GraphicsReadbackOutcome.Verified)]
    [InlineData(true, GraphicsReadbackOutcome.Violation)]
    public void Default_sensor_grants_need_absent_sensor_constructors(bool present, GraphicsReadbackOutcome expected)
    {
        var observation = Observation();
        ((Dictionary<string, object?>)observation["permissions"]!)["accelerometer"] = "granted";
        observation["remainingApis"] = new { Accelerometer = present, Gyroscope = false };
        Assert.Equal(expected, AdditionalFingerprintPrivacy.ReadStartupResult(JsonSerializer.Serialize(observation), true).Outcome);
    }

    [Fact]
    public void Native_request_guard_does_not_bypass_other_api_or_network_verification()
    {
        var observation = Observation();
        ((Dictionary<string, object?>)observation["permissions"]!)["camera"] = "prompt";
        ((Dictionary<string, object?>)observation["apis"]!)["xr"] = true;
        Assert.Equal(GraphicsReadbackOutcome.Violation, AdditionalFingerprintPrivacy.ReadStartupResult(JsonSerializer.Serialize(observation), true).Outcome);
        ((Dictionary<string, object?>)observation["apis"]!)["xr"] = false;
        ((Dictionary<string, object?>)observation["connection"]!)["rtt"] = 500;
        Assert.Equal(GraphicsReadbackOutcome.Violation, AdditionalFingerprintPrivacy.ReadStartupResult(JsonSerializer.Serialize(observation), true).Outcome);
        ((Dictionary<string, object?>)observation["permissions"]!)["camera"] = "NotPerformed";
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, AdditionalFingerprintPrivacy.ReadStartupResult(JsonSerializer.Serialize(observation), true).Outcome);
    }
}
