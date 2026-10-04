using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Native Blink Speech Synthesis restriction, shared by every controller in the environment.</summary>
public static class SpeechPrivacy
{
    public const string BrowserFlag = "--disable-blink-features=ScriptedSpeechSynthesis";
    public static bool IsEnabled(GraphicsPolicy policy) => policy is GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsAndFontAccessExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental or GraphicsPolicy.StrictFingerprintExperimental;
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(SpeechPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.speech-observation.v1.js")
            ?? throw new InvalidOperationException("Speech observation resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(() => {\n" + ObservationScript + "\nreturn collectSpeechObservation();\n})()";

    public static GraphicsReadbackResult ReadResult(string? json)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка Speech Synthesis недоступна.");
        if (json is null) return unavailable;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unavailable;
            var names = new[] { "synthesisAvailable", "synthesisConstructorAvailable", "utteranceConstructorAvailable", "voiceConstructorAvailable" };
            if (names.Any(name => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True))
                return new(GraphicsReadbackOutcome.Violation, "Speech Synthesis остаётся доступен.");
            if (names.Any(name => !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.False)) return unavailable;
            return new(GraphicsReadbackOutcome.Verified, "Speech Synthesis API недоступен в этом документе.");
        }
        catch (JsonException) { return unavailable; }
    }
}
