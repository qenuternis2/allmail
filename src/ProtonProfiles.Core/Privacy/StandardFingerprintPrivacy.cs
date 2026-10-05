using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Native document preferences; this does not hide physical screen, RAM or installed fonts.</summary>
public static class StandardFingerprintPrivacy
{
    public static bool IsEnabled(GraphicsPolicy policy) => AdditionalFingerprintPrivacy.IsEnabled(policy);
    private static readonly string[] LegacyHints = ["Device-Memory", "DPR", "Width", "Viewport-Width", "Viewport-Height", "RTT", "Downlink", "ECT"];
    public static bool IsClientHintHeader(string name) => name.StartsWith("Sec-CH-",StringComparison.OrdinalIgnoreCase)
        || LegacyHints.Contains(name,StringComparer.OrdinalIgnoreCase);
    public static readonly IReadOnlyDictionary<string, string> MediaFeatures = new Dictionary<string, string>
    {
        ["prefers-color-scheme"] = "light", ["prefers-contrast"] = "no-preference",
        ["prefers-reduced-motion"] = "no-preference", ["prefers-reduced-data"] = "no-preference",
        ["prefers-reduced-transparency"] = "no-preference", ["forced-colors"] = "none", ["color-gamut"] = "srgb"
    };
    public static readonly IReadOnlyDictionary<string, string> FontFamilies = new Dictionary<string, string>
    {
        ["standard"] = "Times New Roman", ["serif"] = "Times New Roman", ["sansSerif"] = "Arial",
        ["fixed"] = "Courier New", ["cursive"] = "Comic Sans MS", ["fantasy"] = "Impact", ["math"] = "Cambria Math"
    };
    public static IEnumerable<(string Method, string Arguments)> Commands(bool allowLocalFonts = false, bool allowScreenWorkArea = false)
    {
        yield return ("Emulation.setEmulatedMedia", JsonSerializer.Serialize(new { media = "", features = MediaFeatures.Select(p => new { name = p.Key, value = p.Value }) }));
        if(!allowScreenWorkArea)yield return ("Emulation.setDevicePostureOverride", "{\"posture\":{\"type\":\"continuous\"}}");
        yield return ("Page.setFontFamilies", JsonSerializer.Serialize(new { fontFamilies = FontFamilies }));
        yield return ("Page.setFontSizes", "{\"fontSizes\":{\"standard\":16,\"fixed\":13}}");
        yield return ("Emulation.setEmulatedOSTextScale", "{\"scale\":1}");
        // CSS instrumentation must stay enabled: merely setting the flag does not register its probe.
        if(allowLocalFonts)yield break;
        yield return ("DOM.enable", "{}");
        yield return ("CSS.enable", "{}");
        yield return ("CSS.setLocalFontsEnabled", "{\"enabled\":false}");
    }
    private static readonly Lazy<string> Observation = new(() =>
    {
        using var stream = typeof(StandardFingerprintPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.standard-fingerprint-observation.v1.js")
            ?? throw new InvalidOperationException("Standard fingerprint observation resource is missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(async () => {\n" + ObservationScript + "\nreturn await collectStandardFingerprintObservation();\n})()";
    public static GraphicsReadbackResult ReadResult(string? json, bool allowLocalFonts = false, bool allowScreenWorkArea = false)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка стандартных CSS-параметров и local(...) не выполнена.");
        try
        {
            using var document = JsonDocument.Parse(json ?? "null"); var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "Observed" || root.GetProperty("documentContext").ValueKind != JsonValueKind.True) return unavailable;
            var display = allowScreenWorkArea ? GraphicsReadbackOutcome.Verified : ReadDisplayPosture(root.GetProperty("displayPosture"), true);
            if(display==GraphicsReadbackOutcome.Unavailable)return unavailable;
            if(display==GraphicsReadbackOutcome.Violation)return new(GraphicsReadbackOutcome.Violation,"Положение устройства или сегменты viewport не стандартизованы.");
            var mismatch = new GraphicsReadbackResult(GraphicsReadbackOutcome.Violation, "Стандартные CSS-параметры, generic-шрифты или запрет local(...) не подтверждены.");
            foreach (var pair in MediaFeatures)
            {
                var value = root.GetProperty("media").GetProperty(pair.Key);
                if (pair.Key is "prefers-reduced-data" or "prefers-reduced-transparency" && value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) return unavailable;
                if (value.ValueKind == JsonValueKind.False) return mismatch;
            }
            foreach (var key in new[] { "serif", "sansSerif", "fixed", "cursive", "fantasy", "math" })
            {
                var value = root.GetProperty("genericFonts").GetProperty(key);
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) return unavailable;
                if (value.ValueKind == JsonValueKind.False) return mismatch;
            }
            var blocked=root.TryGetProperty("localFontConstructionBlocked",out var construction) && construction.ValueKind==JsonValueKind.True;
            if (!allowLocalFonts && !blocked && root.GetProperty("localFontLoad").ValueKind != JsonValueKind.True) return unavailable;
            var local = root.GetProperty("localFontRendering");
            if (!allowLocalFonts && !blocked && local.ValueKind != JsonValueKind.False && local.ValueKind != JsonValueKind.True) return unavailable;
            if (!root.GetProperty("defaultFontSize").TryGetDouble(out var fontSize)) return unavailable;
            if (fontSize != 16) return mismatch;
            var scale = root.GetProperty("osTextScale");
            if (scale.ValueKind != JsonValueKind.Null)
            {
                if (!scale.TryGetDouble(out var number)) return unavailable;
                if (number != 1) return mismatch;
            }
            return allowLocalFonts || blocked || local.ValueKind == JsonValueKind.False
                ? new(GraphicsReadbackOutcome.Verified, allowLocalFonts ? "CSS-параметры стандартизованы; локальные шрифты разрешены исключением профиля." : blocked
                    ? "CSS-параметры стандартизованы; FontFace(local) ограничен скриптом. Другие CSS-пути определения шрифтов не скрыты."
                    : "CSS-предпочтения и generic-шрифты стандартизованы; отрисовка local(...) ограничена в документе. Наличие шрифта не скрыто.") : mismatch;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return unavailable; }
    }
    public static GraphicsReadbackOutcome ReadDisplayPosture(JsonElement o,bool documentContext)
    {
        try {
            if(o.GetProperty("status").GetString()=="NotApplicable")return documentContext?GraphicsReadbackOutcome.Unavailable:GraphicsReadbackOutcome.Verified;
            if(o.GetProperty("status").GetString()!="Observed")return GraphicsReadbackOutcome.Unavailable;
            var media=o.GetProperty("media");var good=true;
            foreach(var key in new[]{"continuous","folded","horizontalSingle","horizontalDouble","verticalSingle","verticalDouble"}) {
                var value=media.GetProperty(key);
                if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return GraphicsReadbackOutcome.Unavailable;
                good &= value.GetBoolean()==(key is "continuous");
            }
            var css=o.GetProperty("css");
            if(css.GetProperty("posture").ValueKind!=JsonValueKind.String||!css.GetProperty("horizontal").TryGetInt32(out var h)||!css.GetProperty("vertical").TryGetInt32(out var v))return GraphicsReadbackOutcome.Unavailable;
            var env=css.GetProperty("envSupported");var left=css.GetProperty("secondSegmentLeft");
            if(env.ValueKind is not (JsonValueKind.True or JsonValueKind.False)|| (env.GetBoolean()?left.ValueKind!=JsonValueKind.Number||!left.TryGetDouble(out var nleft)||!double.IsFinite(nleft):left.ValueKind!=JsonValueKind.Null))return GraphicsReadbackOutcome.Unavailable;
            good &= css.GetProperty("posture").GetString()=="continuous"&&h==0&&v==0&&(!env.GetBoolean()||left.GetDouble()==777);
            var pa=o.GetProperty("postureApiAvailable");var va=o.GetProperty("viewportApiAvailable");
            if(pa.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||va.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return GraphicsReadbackOutcome.Unavailable;
            var pt=o.GetProperty("postureType");var pg=o.GetProperty("nativePostureGetter");
            if(pa.GetBoolean()) {
                if(pt.ValueKind!=JsonValueKind.String||pg.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return GraphicsReadbackOutcome.Unavailable;
                good &= pt.GetString()=="continuous"&&pg.GetBoolean();
            }else if(pt.ValueKind!=JsonValueKind.Null||pg.ValueKind!=JsonValueKind.Null)return GraphicsReadbackOutcome.Unavailable;
            var count=o.GetProperty("segmentCount");var vg=o.GetProperty("nativeSegmentsGetter");
            if(va.GetBoolean()) {
                if(vg.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||count.ValueKind!=JsonValueKind.Null&&(!count.TryGetInt32(out var n)||n<1))return GraphicsReadbackOutcome.Unavailable;
                good &= vg.GetBoolean()&&(count.ValueKind==JsonValueKind.Null||count.GetInt32()==1);
            }else if(vg.ValueKind!=JsonValueKind.Null||count.ValueKind!=JsonValueKind.Null)return GraphicsReadbackOutcome.Unavailable;
            good &= !va.GetBoolean();
            return good?GraphicsReadbackOutcome.Verified:GraphicsReadbackOutcome.Violation;
        }catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return GraphicsReadbackOutcome.Unavailable;}
    }
}
