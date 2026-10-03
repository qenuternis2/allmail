using System.Text.Json;

namespace ProtonProfiles.Core.Privacy;

public static class CanvasReadback
{
    private static readonly Lazy<string> Source = new(() =>
    {
        using var stream = typeof(CanvasReadback).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.canvas-readback.v1.js")
            ?? throw new InvalidOperationException("Canvas readback resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Script => Source.Value;
    public static string EvaluationScript => "(async () => {\n" + Script + "\nreturn await collectCanvasReadback();\n})()";

    // Runtime.evaluate with awaitPromise returns a CDP envelope, not ExecuteScriptAsync JSON.
    public static GraphicsReadbackResult ReadCdpResult(string? json)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка чтения Canvas недоступна.");
        if (json is null) return unavailable;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("exceptionDetails", out _)
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("value", out var observation) || observation.ValueKind != JsonValueKind.Object) return unavailable;
            var checks = new[] { "htmlGetImageData", "htmlToDataURL", "htmlToBlob", "offscreenGetImageData", "offscreenConvertToBlob" };
            var readable = checks.Where(name => observation.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() == "Readable").ToArray();
            if (readable.Length > 0)
                return new(GraphicsReadbackOutcome.Violation, "Чтение Canvas доступно: " + string.Join(", ", readable) + ".");
            if (!observation.TryGetProperty("htmlSupported", out var html) || html.ValueKind != JsonValueKind.True
                || !observation.TryGetProperty("htmlDrawing", out var drawing) || drawing.ValueKind != JsonValueKind.True
                || !observation.TryGetProperty("offscreenSupported", out var offscreen)
                || offscreen.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unavailable;
            if (!observation.TryGetProperty("offscreenDrawing", out var offscreenDrawing)
                || offscreenDrawing.ValueKind != (offscreen.GetBoolean() ? JsonValueKind.True : JsonValueKind.Null)) return unavailable;
            foreach (var name in checks)
            {
                var expected = name.StartsWith("offscreen", StringComparison.Ordinal) && !offscreen.GetBoolean() ? "NotApplicable" : "Blocked";
                if (!observation.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() != expected) return unavailable;
            }
            return new(GraphicsReadbackOutcome.Verified, "Чтение Canvas в проверенном документе заблокировано; рисование доступно.");
        }
        catch (JsonException) { return unavailable; }
    }
}
