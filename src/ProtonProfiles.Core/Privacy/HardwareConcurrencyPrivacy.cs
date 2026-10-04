using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Native CDP count normalization. Does not change physical processors or hide benchmark results.</summary>
public static class HardwareConcurrencyPrivacy
{
    public static bool IsEnabled(GraphicsPolicy policy) => policy is GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessAndCpuExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndDevicesExperimental;
    public static int Normalize(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        return count >= 8 ? 8 : count >= 4 ? 4 : count >= 2 ? 2 : 1;
    }
    public static bool IsBucket(int count) => count is 1 or 2 or 4 or 8;
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(HardwareConcurrencyPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.cpu-observation.v1.js")
            ?? throw new InvalidOperationException("CPU observation resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(() => {\n" + ObservationScript + "\nreturn collectCpuObservation();\n})()";

    public static int ReadNativeCdpCount(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("exceptionDetails",out _)
                && root.TryGetProperty("result",out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("type",out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "number"
                && result.TryGetProperty("value",out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var count) && count > 0) return count;
        }
        catch (JsonException) { }
        throw new InvalidOperationException("Нативное число потоков CPU определить не удалось.");
    }

    public static GraphicsReadbackResult ReadResult(string? json, int? expected)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable,"Проверка округления CPU недоступна.");
        if (json is null || expected is null || !IsBucket(expected.Value)) return unavailable;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status",out var status)
                || status.ValueKind != JsonValueKind.String || status.GetString() != "Observed"
                || !root.TryGetProperty("hardwareConcurrency",out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var count) || count <= 0
                || !root.TryGetProperty("nativeGetter",out var getter) || getter.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("ownProperty",out var own) || own.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unavailable;
            return count == expected && getter.ValueKind == JsonValueKind.True && own.ValueKind == JsonValueKind.False
                ? new(GraphicsReadbackOutcome.Verified,"Округлённое число потоков CPU и нативный getter подтверждены в этом контексте.")
                : new(GraphicsReadbackOutcome.Violation,"Число потоков CPU или нативный getter не соответствуют запрошенному ограничению.");
        }
        catch (JsonException) { return unavailable; }
    }
}
