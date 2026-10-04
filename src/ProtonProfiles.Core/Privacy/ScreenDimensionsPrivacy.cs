using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Native desktop screen emulation. Viewport size and physical displays remain unchanged.</summary>
public static class ScreenDimensionsPrivacy
{
    public const int Width = 1920;
    public const int Height = 1080;
    public static bool IsEnabled(GraphicsPolicy policy) => policy == GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuAndScreenExperimental;
    // Zero viewport dimensions and scale preserve WebView2's responsive viewport and native DPR.
    // This command is only valid for a top-level target; Chromium propagates screen information to frames.
    public static string Parameters => JsonSerializer.Serialize(new {
        width = 0, height = 0, deviceScaleFactor = 0, mobile = false,
        screenWidth = Width, screenHeight = Height, positionX = 0, positionY = 0,
        dontSetVisibleSize = true, screenOrientation = new {type = "landscapePrimary", angle = 0}
    });

    public static GraphicsReadbackResult ReadResult(string? json)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка размеров экрана недоступна.");
        if (json is null) return unavailable;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("screenApisAvailable", out var available)
                || available.ValueKind != JsonValueKind.True) return unavailable;
            var expected = new Dictionary<string,int> { ["width"] = Width, ["height"] = Height,
                ["availWidth"] = Width, ["availHeight"] = Height, ["availLeft"] = 0, ["availTop"] = 0,
                ["orientationAngle"] = 0 };
            var matches = true;
            foreach (var (key, number) in expected)
            {
                if (!root.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.Number
                    || !field.TryGetInt32(out var observed)) return unavailable;
                matches &= observed == number;
            }
            foreach (var key in new[] {"deviceWidthMatches", "deviceHeightMatches", "nativeGetters", "ownProperties"})
            {
                if (!root.TryGetProperty(key, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unavailable;
                matches &= field.GetBoolean() == (key != "ownProperties");
            }
            if (!root.TryGetProperty("orientationType", out var orientation) || orientation.ValueKind != JsonValueKind.String) return unavailable;
            matches &= orientation.GetString() == "landscape-primary";
            return matches ? new(GraphicsReadbackOutcome.Verified, "Экран 1920×1080, доступная область и нативные getters подтверждены в этом документе.")
                : new(GraphicsReadbackOutcome.Violation, "Размеры экрана, CSS media queries или нативные getters не соответствуют выбранному режиму.");
        }
        catch (JsonException) { return unavailable; }
    }
}
