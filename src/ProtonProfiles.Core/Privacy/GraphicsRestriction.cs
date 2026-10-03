using System.Text.Json;

namespace ProtonProfiles.Core.Privacy;

public enum GraphicsReadbackOutcome { Verified, Violation, Unavailable }

public sealed record GraphicsReadbackResult(GraphicsReadbackOutcome Outcome, string Detail);

public static class GraphicsRestriction
{
    // Only an owned blank document is used for this synchronous startup check.
    // WebGPU requires a secure origin and is observed separately on the HTTPS probe.
    public const string WebGlVerificationScript = """
        (() => {
          const check = (create, type) => {
            try { return !!create().getContext(type); } catch { return null; }
          };
          const offscreenSupported = typeof OffscreenCanvas === 'function';
          return {
            canvasWebGl: check(() => document.createElement('canvas'), 'webgl'),
            canvasExperimentalWebGl: check(() => document.createElement('canvas'), 'experimental-webgl'),
            canvasWebGl2: check(() => document.createElement('canvas'), 'webgl2'),
            offscreenSupported,
            offscreenWebGl: offscreenSupported ? check(() => new OffscreenCanvas(1, 1), 'webgl') : null,
            offscreenWebGl2: offscreenSupported ? check(() => new OffscreenCanvas(1, 1), 'webgl2') : null
          };
        })()
        """;

    public static GraphicsReadbackResult ReadWebGlResult(string? json)
    {
        var unavailable = new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable, "Проверка WebGL недоступна.");
        if (json is null) return unavailable;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unavailable;
            var names = new[] { "canvasWebGl", "canvasExperimentalWebGl", "canvasWebGl2", "offscreenWebGl", "offscreenWebGl2" };
            var labels = new[] { "Canvas WebGL", "Canvas experimental-webgl", "Canvas WebGL2", "OffscreenCanvas WebGL", "OffscreenCanvas WebGL2" };
            var active = names.Select((name, index) => (name, index))
                .Where(x => root.TryGetProperty(x.name, out var value) && value.ValueKind == JsonValueKind.True)
                .Select(x => labels[x.index]).ToList();
            if (active.Count > 0)
                return new(GraphicsReadbackOutcome.Violation, "Доступны: " + string.Join(", ", active) + ".");
            if (!root.TryGetProperty("offscreenSupported", out var supported)
                || supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unavailable;
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var value)) return unavailable;
                var expected = name.StartsWith("offscreen", StringComparison.Ordinal) && !supported.GetBoolean()
                    ? JsonValueKind.Null : JsonValueKind.False;
                if (value.ValueKind != expected) return unavailable;
            }
            return new(GraphicsReadbackOutcome.Verified, "В проверенном документе WebGL-контексты недоступны.");
        }
        catch (JsonException) { return unavailable; }
    }
}
