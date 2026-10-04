using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Explicit script restrictions for APIs without native WebView2 switches. Not an undetectable fingerprint.</summary>
public static class ResidualFingerprintPrivacy
{
    public static bool IsEnabled(GraphicsPolicy policy) => AdditionalFingerprintPrivacy.IsEnabled(policy);
    private static string Read(string name)
    {
        using var stream=typeof(ResidualFingerprintPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy."+name)
            ?? throw new InvalidOperationException("Residual privacy resource missing.");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    private static readonly Lazy<string> Guard=new(()=>Read("residual-fingerprint-guard.v1.js"));
    private static readonly Lazy<string> Observer=new(()=>Read("residual-fingerprint-observation.v1.js"));
    public static string Script=>Guard.Value;
    public static string ObservationScript=>Observer.Value;
    public static string EvaluationScript=>"(() => {\n"+ObservationScript+"\nreturn collectResidualFingerprintObservation();\n})()";
    public static GraphicsReadbackResult ReadResult(string? json)
    {
        var unavailable=new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable,"Проверка программных ограничений не выполнена.");
        try
        {
            using var doc=JsonDocument.Parse(json??"null");var o=doc.RootElement;
            if(o.GetProperty("status").GetString()!="Observed")return unavailable;
            var good=o.GetProperty("deviceMemory").GetDouble()==8 && o.GetProperty("scriptRestriction").ValueKind==JsonValueKind.True;
            foreach(var (group,keys) in new[]{
                ("navigatorApis",new[]{"getBattery","getGamepads","mediaDevices","mediaCapabilities","serviceWorker"}),
                ("constructors",new[]{"BatteryManager","Gamepad","GamepadButton","GamepadEvent","GamepadHapticActuator","MediaDevices","MediaDeviceInfo","InputDeviceInfo","MediaCapabilities","Accelerometer","LinearAccelerationSensor","GravitySensor","Gyroscope","AbsoluteOrientationSensor","RelativeOrientationSensor","IdleDetector","ScreenDetails","ScreenDetailed","MemoryInfo","ServiceWorker","ServiceWorkerContainer","ServiceWorkerRegistration"})})
            {
                var entries=o.GetProperty(group);
                if(entries.EnumerateObject().Count()!=keys.Length)return unavailable;
                foreach(var key in keys)
                {
                    var value=entries.GetProperty(key);
                    if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                    good &= value.ValueKind==JsonValueKind.False;
                }
            }
            foreach(var key in new[]{"performanceMemoryAvailable","storageEstimateAvailable","getScreenDetailsAvailable"})
            {
                if(o.GetProperty(key).ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= o.GetProperty(key).ValueKind==JsonValueKind.False;
            }
            foreach(var key in new[]{"AudioDecoder","VideoDecoder","AudioEncoder","VideoEncoder","AudioData","VideoFrame","EncodedAudioChunk","EncodedVideoChunk"})
            {
                var value=o.GetProperty("webCodecs").GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= value.ValueKind==JsonValueKind.False;
            }
            foreach(var key in new[]{"keyboard","Keyboard","KeyboardLayoutMap","getLayoutMap","lock","unlock"})
            {
                var value=o.GetProperty("keyboardLayout").GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= value.ValueKind==JsonValueKind.False;
            }
            var font=o.GetProperty("localFontConstructionBlocked");
            if(font.ValueKind!=JsonValueKind.Null && font.ValueKind!=JsonValueKind.True && font.ValueKind!=JsonValueKind.False)return unavailable;
            good &= font.ValueKind is JsonValueKind.Null or JsonValueKind.True;
            return good?new(GraphicsReadbackOutcome.Verified,"RAM bucket 8 и программные ограничения API/WebCodecs/раскладки клавиатуры подтверждены; изменения JavaScript обнаружимы.")
                :new(GraphicsReadbackOutcome.Violation,"Программные ограничения RAM, устройств, клавиатуры или шрифтов не подтверждены.");
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return unavailable;}
    }
}
