using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Disables Blink Compute Pressure. Does not hide benchmarks or actual CPU load.</summary>
public static class ComputePressurePrivacy
{
    public const string BlinkFeature = "ComputePressure";
    public static bool IsEnabled(GraphicsPolicy policy) => policy is GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental or GraphicsPolicy.StrictFingerprintExperimental;
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(ComputePressurePrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.compute-pressure-observation.v1.js")
            ?? throw new InvalidOperationException("Compute Pressure observation resource is missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(() => {\n" + ObservationScript + "\nreturn collectComputePressureObservation();\n})()";
    public static GraphicsReadbackResult ReadResult(string? json, bool worker = false)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable,"Проверка Compute Pressure недоступна.");
        if (json is null) return unavailable;
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status",out var status)
                || status.ValueKind != JsonValueKind.String || status.GetString() != "Observed"
                || !root.TryGetProperty("secureContext",out var secure) || secure.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("documentContext",out var context)
                || context.ValueKind != (worker ? JsonValueKind.False : JsonValueKind.True)) return unavailable;
            foreach (var key in new[] {"observerAvailable","recordAvailable"})
                if (root.TryGetProperty(key,out var field) && field.ValueKind == JsonValueKind.True)
                    return new(GraphicsReadbackOutcome.Violation,"Compute Pressure API остаётся доступен.");
            foreach (var key in new[] {"observerAvailable","recordAvailable"})
                if (!root.TryGetProperty(key,out var field) || field.ValueKind != JsonValueKind.False) return unavailable;
            return new(GraphicsReadbackOutcome.Verified,"PressureObserver и PressureRecord недоступны в этом защищённом контексте.");
        }
        catch (JsonException) { return unavailable; }
    }
}
