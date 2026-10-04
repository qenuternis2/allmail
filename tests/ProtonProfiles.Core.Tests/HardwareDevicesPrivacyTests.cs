using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;
namespace ProtonProfiles.Core.Tests;
public class HardwareDevicesPrivacyTests
{
    private const GraphicsPolicy Mode = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental;
    private static Dictionary<string,object?> Absent(bool worker = false) => new() {
        ["status"]="Observed", ["secureContext"]=true, ["documentContext"]=!worker,
        ["navigatorApis"]=new Dictionary<string,bool> {{"bluetooth",false},{"usb",false},{"hid",false},{"serial",false}},
        ["constructors"]=new[] {"Bluetooth","BluetoothDevice","BluetoothUUID","BluetoothRemoteGATTServer","BluetoothRemoteGATTService","BluetoothRemoteGATTCharacteristic","BluetoothRemoteGATTDescriptor","BluetoothAdvertisingEvent","USB","USBDevice","USBConnectionEvent","USBInTransferResult","USBOutTransferResult","USBIsochronousInTransferPacket","USBIsochronousInTransferResult","USBIsochronousOutTransferPacket","USBIsochronousOutTransferResult","HID","HIDDevice","HIDConnectionEvent","HIDInputReportEvent","Serial","SerialPort"}.ToDictionary(name=>name,_=>false)
    };
    [Fact]
    public void Device_mode_retains_previous_restrictions_and_persists_with_restart_and_native_UA()
    {
        foreach(var policy in Enum.GetValues<GraphicsPolicy>()) Assert.Equal(policy==Mode,HardwareDevicesPrivacy.IsEnabled(policy));
        var before=BrowserArguments.Build(null,graphics:GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental);
        var after=BrowserArguments.Build(null,graphics:Mode);
        Assert.Equal(before.Replace(",SharedWorker,FontAccess",",SharedWorker,FontAccess,"+HardwareDevicesPrivacy.BlinkFeatures,StringComparison.Ordinal),after);
        Assert.Equal(1,after.Split("--disable-blink-features=").Length-1);
        Assert.True(HardwareConcurrencyPrivacy.IsEnabled(Mode));Assert.True(FontAccessPrivacy.IsEnabled(Mode));
        Assert.True(AudioPageGuard.IsEnabled(Mode));Assert.True(ScreenPrivacy.IsEnabled(Mode));Assert.True(UserAgentHintsPrivacy.IsEnabled(Mode));
        using var env=new TestEnv();var original=env.AddProfile();var edited=original with {GraphicsPolicy=Mode};
        Assert.True(env.Catalog.SaveSettings(edited,profileIsLive:true).RestartRequired);
        Assert.Equal(Mode,env.Repository.Get(original.Id)!.GraphicsPolicy);
        Assert.Contains(UserAgentHintsPrivacy.CustomUserAgentError,ProfileValidator.Validate(edited with {UserAgentMode=UserAgentMode.Custom,CustomUserAgent="Chosen/1.0"}));
    }
    [Fact]
    public void Every_observed_entry_point_must_be_absent_in_the_correct_secure_context()
    {
        foreach(var worker in new[]{false,true})
        {
            var json=JsonSerializer.Serialize(Absent(worker));
            Assert.Equal(GraphicsReadbackOutcome.Verified,HardwareDevicesPrivacy.ReadResult(json,worker).Outcome);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareDevicesPrivacy.ReadResult(json,!worker).Outcome);
        }
        foreach(var group in new[]{"navigatorApis","constructors"})
            foreach(var name in ((Dictionary<string,bool>)Absent()[group]!).Keys)
            {
                var present=Absent();((Dictionary<string,bool>)present[group]!)[name]=true;
                Assert.Equal(GraphicsReadbackOutcome.Violation,HardwareDevicesPrivacy.ReadResult(JsonSerializer.Serialize(present)).Outcome);
                var partial=Absent();((Dictionary<string,bool>)partial[group]!).Remove(name);
                Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareDevicesPrivacy.ReadResult(JsonSerializer.Serialize(partial)).Outcome);
            }
        foreach(var key in Absent().Keys)
        {
            var partial=Absent();partial.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareDevicesPrivacy.ReadResult(JsonSerializer.Serialize(partial)).Outcome);
        }
        var insecure=Absent();insecure["secureContext"]=false;
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareDevicesPrivacy.ReadResult(JsonSerializer.Serialize(insecure)).Outcome);
    }
    [Theory]
    [InlineData(null)] [InlineData("null")] [InlineData("[]")] [InlineData("{}")]
    [InlineData("{broken")] [InlineData("{\"status\":\"Observed\",\"secureContext\":true,\"documentContext\":true,\"navigatorApis\":[],\"constructors\":false}")]
    public void Missing_or_malformed_evidence_is_unavailable(string? json) =>
        Assert.Equal(GraphicsReadbackOutcome.Unavailable,HardwareDevicesPrivacy.ReadResult(json).Outcome);
}
