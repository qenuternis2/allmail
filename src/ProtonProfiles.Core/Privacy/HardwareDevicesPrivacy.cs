using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Disables Blink hardware device APIs; never enumerates or connects to devices.</summary>
public static class HardwareDevicesPrivacy
{
    public const string BlinkFeatures = "WebBluetooth,WebUSB,WebHID,Serial";
    public static bool IsEnabled(GraphicsPolicy policy) => policy is GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental;
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(HardwareDevicesPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.hardware-devices-observation.v1.js")
            ?? throw new InvalidOperationException("Hardware devices observation resource is missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(() => {\n" + ObservationScript + "\nreturn collectHardwareDevicesObservation();\n})()";
    public static GraphicsReadbackResult ReadResult(string? json, bool worker = false)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable,"Проверка аппаратных API недоступна.");
        if (json is null) return unavailable;
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status",out var status)
                || status.ValueKind != JsonValueKind.String || status.GetString() != "Observed"
                || !root.TryGetProperty("secureContext",out var secure) || secure.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("documentContext",out var context)
                || context.ValueKind != (worker ? JsonValueKind.False : JsonValueKind.True)) return unavailable;
            var groups = new Dictionary<string,string[]> {
                ["navigatorApis"] = ["bluetooth","usb","hid","serial"],
                ["constructors"] = ["Bluetooth","BluetoothDevice","BluetoothUUID","BluetoothRemoteGATTServer",
                    "BluetoothRemoteGATTService","BluetoothRemoteGATTCharacteristic","BluetoothRemoteGATTDescriptor","BluetoothAdvertisingEvent",
                    "USB","USBDevice","USBConnectionEvent","USBInTransferResult","USBOutTransferResult","USBIsochronousInTransferPacket",
                    "USBIsochronousInTransferResult","USBIsochronousOutTransferPacket","USBIsochronousOutTransferResult",
                    "HID","HIDDevice","HIDConnectionEvent","HIDInputReportEvent","Serial","SerialPort"]
            };
            var missing = false;
            foreach (var (group,names) in groups)
            {
                if (!root.TryGetProperty(group,out var observed) || observed.ValueKind != JsonValueKind.Object) {missing = true;continue;}
                foreach (var name in names)
                {
                    if (!observed.TryGetProperty(name,out var field)) {missing = true;continue;}
                    if (field.ValueKind == JsonValueKind.True) return new(GraphicsReadbackOutcome.Violation,"Bluetooth/USB/HID/Serial API остаётся доступен.");
                    missing |= field.ValueKind != JsonValueKind.False;
                }
            }
            return missing ? unavailable : new(GraphicsReadbackOutcome.Verified,"Bluetooth/USB/HID/Serial API недоступны в этом защищённом контексте.");
        }
        catch (JsonException) { return unavailable; }
    }
}
