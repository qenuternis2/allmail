using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Native CDP screen emulation; window/viewport size remains responsive. No page API replacements.</summary>
public static class ScreenPrivacy
{
    public const int Width = 1920;
    public const int Height = 1080;
    public static bool IsEnabled(GraphicsPolicy policy) => policy == GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeScreenExperimental;
    public static string Parameters { get; } = JsonSerializer.Serialize(new {
        width = 0, height = 0, deviceScaleFactor = 1.0, mobile = false,
        screenWidth = Width, screenHeight = Height, positionX = 0, positionY = 0,
        dontSetVisibleSize = true, screenOrientation = new { type = "landscapePrimary", angle = 0 },
    });
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
            var expected = new Dictionary<string, double> {
                ["width"] = Width, ["height"] = Height, ["availWidth"] = Width, ["availHeight"] = Height,
                ["availLeft"] = 0, ["availTop"] = 0, ["devicePixelRatio"] = expectedDpr, ["orientationAngle"] = 0,
            };
            var matches = true;
            foreach (var (key, value) in expected)
            {
                if (!root.TryGetProperty(key, out var field) || !field.TryGetDouble(out var number) || !double.IsFinite(number)) return unavailable;
                matches &= Math.Abs(number - value) < 0.001;
            }
            foreach (var key in new[] { "screenApisAvailable", "deviceWidthMatches", "deviceHeightMatches", "resolutionMatches" })
            {
                if (!root.TryGetProperty(key, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unavailable;
                matches &= field.GetBoolean();
            }
            if (!root.TryGetProperty("orientationType", out var orientation) || orientation.ValueKind != JsonValueKind.String) return unavailable;
            matches &= orientation.GetString() == "landscape-primary";
            return matches ? new(GraphicsReadbackOutcome.Verified, "Параметры экрана подтверждены в этом документе.")
                : new(GraphicsReadbackOutcome.Violation, "Параметры экрана отличаются от заданного режима.");
        }
        catch (JsonException) { return unavailable; }
        catch (InvalidOperationException) { return unavailable; }
    }
}
