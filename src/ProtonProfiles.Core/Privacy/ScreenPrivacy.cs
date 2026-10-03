using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Native browser-wide DPR restriction; screen/viewport dimensions remain native. No page API replacements.</summary>
public static class ScreenPrivacy
{
    public static bool IsEnabled(GraphicsPolicy policy) => policy is GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental;
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(ScreenPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.screen-observation.v1.js")
            ?? throw new InvalidOperationException("Screen observation resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(() => {\n" + ObservationScript + "\nreturn collectScreenObservation();\n})()";

    public static GraphicsReadbackResult ReadResult(string? json, double expectedDpr = 1)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка параметров экрана недоступна.");
        if (json is null || !double.IsFinite(expectedDpr) || expectedDpr <= 0) return unavailable;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unavailable;
            if (!root.TryGetProperty("screenApisAvailable", out var available) || available.ValueKind != JsonValueKind.True) return unavailable;
            foreach (var key in new[] { "width", "height", "availWidth", "availHeight", "devicePixelRatio" })
                if (!root.TryGetProperty(key, out var field) || !field.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0) return unavailable;
            var matches = Math.Abs(root.GetProperty("devicePixelRatio").GetDouble() - expectedDpr) < 0.001;
            foreach (var key in new[] { "deviceWidthMatches", "deviceHeightMatches", "resolutionMatches" })
            {
                if (!root.TryGetProperty(key, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unavailable;
                matches &= field.GetBoolean();
            }
            return matches ? new(GraphicsReadbackOutcome.Verified, "DPR и CSS media queries подтверждены в этом документе.")
                : new(GraphicsReadbackOutcome.Violation, "DPR или CSS media queries отличаются от заданного режима.");
        }
        catch (JsonException) { return unavailable; }
        catch (InvalidOperationException) { return unavailable; }
    }
}
