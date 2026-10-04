using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Native optional API restrictions and fixed NQE estimates; physical hardware is not hidden.</summary>
public static class AdditionalFingerprintPrivacy
{
    public const string BlinkFeatures = "WebXR,GetDisplayMedia,SelectAudioOutput,SensorExtraClasses,WebNFC,CpuPerformance,MeasureMemory,PreciseMemoryInfo";
    public const string NetworkFlag = "--force-effective-connection-type=4G";
    public static readonly string[] DeniedPermissions = ["camera", "microphone", "geolocation", "accelerometer", "gyroscope", "magnetometer", "midi", "camera-ptz", "midi-sysex"];
    public static string PermissionArguments(string permission)
    {
        if(!DeniedPermissions.Contains(permission)) throw new ArgumentOutOfRangeException(nameof(permission));
        var descriptor=new Dictionary<string,object> {["name"]=permission};
        if(permission=="camera-ptz") {descriptor["name"]="camera";descriptor["panTiltZoom"]=true;}
        if(permission=="midi-sysex") {descriptor["name"]="midi";descriptor["sysex"]=true;}
        return JsonSerializer.Serialize(new {permission=descriptor,setting="denied"});
    }
    public static bool IsEnabled(GraphicsPolicy policy) => policy == GraphicsPolicy.StrictFingerprintExperimental;
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(AdditionalFingerprintPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.additional-fingerprint-observation.v1.js")
            ?? throw new InvalidOperationException("Additional fingerprint observation resource is missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(async () => {\n" + ObservationScript + "\nreturn await collectAdditionalFingerprintObservation();\n})()";
    public static GraphicsReadbackResult ReadResult(string? json, bool worker = false)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable,"Проверка дополнительных ограничений недоступна.");
        try
        {
            using var doc = JsonDocument.Parse(json ?? "null"); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("status").GetString() != "Observed"
                || root.GetProperty("secureContext").ValueKind != JsonValueKind.True
                || root.GetProperty("documentContext").ValueKind != (worker ? JsonValueKind.False : JsonValueKind.True)) return unavailable;
            var violation = new GraphicsReadbackResult(GraphicsReadbackOutcome.Violation,"Дополнительные ограничения API, разрешений или оценки сети не подтверждены.");
            foreach (var key in new[] {"xr","cpuPerformance","measureMemory","getDisplayMedia","selectAudioOutput","AmbientLightSensor","Magnetometer","UncalibratedMagnetometer","NDEFReader","NDEFRecord","NDEFMessage"})
            {
                var v=root.GetProperty("apis").GetProperty(key);
                if(v.ValueKind==JsonValueKind.True) return violation;
                if(v.ValueKind!=JsonValueKind.False) return unavailable;
            }
            if(!worker) foreach(var name in DeniedPermissions)
            {
                var v=root.GetProperty("permissions").GetProperty(name);
                if(v.ValueKind!=JsonValueKind.String || v.GetString()=="NotPerformed") return unavailable;
                if(v.GetString()!="denied") return violation;
            }
            var network=root.GetProperty("connection");
            if(network.GetProperty("status").GetString()!="Observed" || network.GetProperty("nativeGetters").ValueKind!=JsonValueKind.True) return unavailable;
            if(network.GetProperty("effectiveType").GetString()!="4g" || !network.GetProperty("rtt").TryGetDouble(out var rtt)
                || !network.GetProperty("downlink").TryGetDouble(out var downlink)) return violation;
            return rtt is >= 100 and <= 250 && downlink is >= 1 and <= 2
                ? new(GraphicsReadbackOutcome.Verified,"Дополнительные API недоступны, аппаратные разрешения запрещены, оценки сети стандартизованы.") : violation;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException) { return unavailable; }
    }
}
