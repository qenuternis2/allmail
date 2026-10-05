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
    private static readonly Lazy<string> Guard=new(()=>Read("residual-fingerprint-guard.v1.js").Replace("/*__PP_COARSE_CLOCK_GUARD__*/",Read("coarse-clock-guard.v1.js"),StringComparison.Ordinal));
    private static readonly Lazy<string> Observer=new(()=>Read("residual-fingerprint-observation.v1.js"));
    public static string Script=>Guard.Value;
    public static string ScriptFor(PrivacyException exceptions)=>Script.Replace("/*__PP_PRIVACY_EXCEPTIONS__*/[]",JsonSerializer.Serialize(ProfilePrivacy.Names(exceptions)),StringComparison.Ordinal);
    private static PrivacyException Feature(string key)=>key switch {
        "getBattery" or "BatteryManager"=>PrivacyException.Battery,
        "getGamepads" or "Gamepad" or "GamepadButton" or "GamepadEvent" or "GamepadHapticActuator"=>PrivacyException.Gamepads,
        "mediaDevices" or "MediaDevices" or "MediaDeviceInfo" or "InputDeviceInfo"=>PrivacyException.MediaDevices,
        "mediaCapabilities" or "MediaCapabilities"=>PrivacyException.MediaCapabilities,
        "serviceWorker" or "ServiceWorker" or "ServiceWorkerContainer" or "ServiceWorkerRegistration"=>PrivacyException.ServiceWorkers,_=>PrivacyException.None};
    public static string ObservationScript=>Observer.Value;
    public static string EvaluationScript=>"(() => {\n"+ObservationScript+"\nreturn collectResidualFingerprintObservation();\n})()";
    public static GraphicsReadbackResult ReadResult(string? json,PrivacyException exceptions=PrivacyException.None)
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
                    good &= Feature(key)!=PrivacyException.None&&ProfilePrivacy.Allows(exceptions,Feature(key)) || value.ValueKind==JsonValueKind.False;
                }
            }
            foreach(var key in new[]{"performanceMemoryAvailable","storageEstimateAvailable","getScreenDetailsAvailable"})
            {
                if(o.GetProperty(key).ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= key=="storageEstimateAvailable"&&ProfilePrivacy.Allows(exceptions,PrivacyException.StorageEstimate) || o.GetProperty(key).ValueKind==JsonValueKind.False;
            }
            foreach(var key in new[]{"AudioDecoder","VideoDecoder","AudioEncoder","VideoEncoder","AudioData","VideoFrame","EncodedAudioChunk","EncodedVideoChunk"})
            {
                var value=o.GetProperty("webCodecs").GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= ProfilePrivacy.Allows(exceptions,PrivacyException.WebCodecs) || value.ValueKind==JsonValueKind.False;
            }
            foreach(var key in new[]{"keyboard","Keyboard","KeyboardLayoutMap","getLayoutMap","lock","unlock"})
            {
                var value=o.GetProperty("keyboardLayout").GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= ProfilePrivacy.Allows(exceptions,PrivacyException.KeyboardLayout) || value.ValueKind==JsonValueKind.False;
            }
            foreach(var key in new[]{"html","offscreen"})
            {
                var value=o.GetProperty("canvasTextMetrics").GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= ProfilePrivacy.Allows(exceptions,PrivacyException.CanvasTextMetrics) || value.ValueKind==JsonValueKind.False;
            }
            var fontCheck=o.GetProperty("fontSetCheckAvailable");
            if(fontCheck.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
            good &= ProfilePrivacy.Allows(exceptions,PrivacyException.LocalFonts)||fontCheck.ValueKind==JsonValueKind.False;
            if(!ProfilePrivacy.Allows(exceptions,PrivacyException.HighResolutionTimers)) {
                var clock=ReadCoarseClocks(o.GetProperty("coarseClocks"));
                if(clock==GraphicsReadbackOutcome.Unavailable)return unavailable;
                good &= clock==GraphicsReadbackOutcome.Verified;
                var video=ReadVideoTelemetry(o.GetProperty("videoTelemetry"),o.GetProperty("documentContext").GetBoolean());
                if(video==GraphicsReadbackOutcome.Unavailable)return unavailable;
                good &= video==GraphicsReadbackOutcome.Verified;
            }
            if(!ProfilePrivacy.Allows(exceptions,PrivacyException.ScreenWorkArea)) {
                var area=o.GetProperty("workArea");var status=area.GetProperty("status").GetString();
                if(status=="NotApplicable")good &= o.GetProperty("documentContext").ValueKind==JsonValueKind.False;
                else {
                    if(status!="Observed"||area.GetProperty("normalized").ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                    good &= area.GetProperty("normalized").GetBoolean();
                }
            }
            var font=o.GetProperty("localFontConstructionBlocked");
            if(font.ValueKind!=JsonValueKind.Null && font.ValueKind!=JsonValueKind.True && font.ValueKind!=JsonValueKind.False)return unavailable;
            good &= ProfilePrivacy.Allows(exceptions,PrivacyException.LocalFonts) || font.ValueKind is JsonValueKind.Null or JsonValueKind.True;
            return good?new(GraphicsReadbackOutcome.Verified,"RAM bucket 8 и неисключённые программные ограничения подтверждены; изменения JavaScript обнаружимы.")
                :new(GraphicsReadbackOutcome.Violation,"Программные ограничения RAM, устройств, клавиатуры или шрифтов не подтверждены.");
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return unavailable;}
    }
    public static GraphicsReadbackOutcome ReadCoarseClocks(JsonElement o)
    {
        try {
            if(o.GetProperty("status").GetString()!="Observed")return GraphicsReadbackOutcome.Unavailable;
            var quantum=o.GetProperty("quantumMs");
            if(quantum.ValueKind!=JsonValueKind.Null&&(!quantum.TryGetInt32(out var n)||n!=100))return GraphicsReadbackOutcome.Unavailable;
            var good=quantum.ValueKind==JsonValueKind.Number;
            foreach(var key in new[]{"nowAligned","originAligned","dateNowAligned","dateConstructorAligned","entryAligned","serializedEntryAligned"}) {
                var value=o.GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return GraphicsReadbackOutcome.Unavailable;
                good &= value.GetBoolean();
            }
            foreach(var key in new[]{"eventAligned","temporalAligned","animationFrameLocked"}) {
                var value=o.GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))return GraphicsReadbackOutcome.Unavailable;
                good &= value.ValueKind!=JsonValueKind.False;
            }
            return good?GraphicsReadbackOutcome.Verified:GraphicsReadbackOutcome.Violation;
        }catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return GraphicsReadbackOutcome.Unavailable;}
    }
    public static GraphicsReadbackOutcome ReadVideoTelemetry(JsonElement o,bool documentContext)
    {
        try {
            var status=o.GetProperty("status").GetString();
            if(status is not ("Observed" or "NotApplicable")||status=="NotApplicable"&&documentContext)return GraphicsReadbackOutcome.Unavailable;
            var good=true;
            foreach(var key in new[]{"requestVideoFrameCallback","cancelVideoFrameCallback","getVideoPlaybackQuality","webkitDecodedFrameCount","webkitDroppedFrameCount"}) {
                var value=o.GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return GraphicsReadbackOutcome.Unavailable;
                good &= !value.GetBoolean();
            }
            return good?GraphicsReadbackOutcome.Verified:GraphicsReadbackOutcome.Violation;
        }catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return GraphicsReadbackOutcome.Unavailable;}
    }
}
