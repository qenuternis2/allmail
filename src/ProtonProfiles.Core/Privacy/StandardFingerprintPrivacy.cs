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
    public static IEnumerable<(string Method, string Arguments)> Commands()
    {
        yield return ("Emulation.setEmulatedMedia", JsonSerializer.Serialize(new { media = "", features = MediaFeatures.Select(p => new { name = p.Key, value = p.Value }) }));
        yield return ("Page.setFontFamilies", JsonSerializer.Serialize(new { fontFamilies = FontFamilies }));
        yield return ("Page.setFontSizes", "{\"fontSizes\":{\"standard\":16,\"fixed\":13}}");
        yield return ("Emulation.setEmulatedOSTextScale", "{\"scale\":1}");
        // CSS instrumentation must stay enabled: merely setting the flag does not register its probe.
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
    public static GraphicsReadbackResult ReadResult(string? json)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка стандартных CSS-параметров и local(...) не выполнена.");
        try
        {
            using var document = JsonDocument.Parse(json ?? "null"); var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "Observed" || root.GetProperty("documentContext").ValueKind != JsonValueKind.True) return unavailable;
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
            if (root.GetProperty("localFontLoad").ValueKind != JsonValueKind.True) return unavailable;
            var local = root.GetProperty("localFontRendering");
            if (local.ValueKind != JsonValueKind.False && local.ValueKind != JsonValueKind.True) return unavailable;
            if (!root.GetProperty("defaultFontSize").TryGetDouble(out var fontSize)) return unavailable;
            if (fontSize != 16) return mismatch;
            var scale = root.GetProperty("osTextScale");
            if (scale.ValueKind != JsonValueKind.Null)
            {
                if (!scale.TryGetDouble(out var number)) return unavailable;
                if (number != 1) return mismatch;
            }
            return local.ValueKind == JsonValueKind.False
                ? new(GraphicsReadbackOutcome.Verified, "CSS-предпочтения и generic-шрифты стандартизованы; отрисовка local(...) ограничена в документе. Наличие шрифта не скрыто.") : mismatch;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return unavailable; }
    }
}
