using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Disables Blink Local Font Access. CSS font detection remains possible.</summary>
public static class FontAccessPrivacy
{
    public static bool IsEnabled(GraphicsPolicy policy) => policy is GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental or GraphicsPolicy.StrictFingerprintExperimental;
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(FontAccessPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.font-access-observation.v1.js")
            ?? throw new InvalidOperationException("Font observation resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(() => {\n" + ObservationScript + "\nreturn collectFontAccessObservation();\n})()";

    public static GraphicsReadbackResult ReadResult(string? json)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка Local Font Access недоступна.");
        try
        {
            if (json is null) return unavailable;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String || status.GetString() != "Observed"
                || !root.TryGetProperty("secureContext", out var secure) || secure.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("documentContext", out var document) || document.ValueKind != JsonValueKind.True) return unavailable;
            var names = new[] {"queryLocalFontsAvailable", "fontDataAvailable"};
            if (names.Any(n => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True))
                return new(GraphicsReadbackOutcome.Violation, "Local Font Access остаётся доступен.");
            if (names.Any(n => !root.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.False)) return unavailable;
            return new(GraphicsReadbackOutcome.Verified, "Local Font Access API недоступен в этом защищённом документе; CSS-проверка шрифтов остаётся доступной.");
        }
        catch (JsonException) { return unavailable; }
    }
}
