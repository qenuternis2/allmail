using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Reapply the effective UA through the native WebView2 setting; runtime readback is mandatory.</summary>
public static class UserAgentHintsPrivacy
{
    public static bool IsEnabled(GraphicsPolicy policy) => policy == GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental;
    public static string UserAgentToApply(ProfileConfig config, string nativeUserAgent)
    {
        var value = config.UserAgentMode == UserAgentMode.Custom ? config.CustomUserAgent : nativeUserAgent;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new ArgumentException("User-Agent недоступен или содержит управляющие символы.");
        return value;
    }
    private static readonly Lazy<string> Observation = new(() => {
        using var stream = typeof(UserAgentHintsPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.ua-hints-observation.v1.js")
            ?? throw new InvalidOperationException("UA hints observation resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    public static string ObservationScript => Observation.Value;
    public static string EvaluationScript => "(async () => {\n" + ObservationScript + "\nreturn await collectUaHintsObservation();\n})()";
    public static GraphicsReadbackResult ReadCdpResult(string? json, string expectedUserAgent)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "null");
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("exceptionDetails", out _)
                && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("value", out var value)) return ReadResult(value.GetRawText(), expectedUserAgent);
        }
        catch (JsonException) { }
        return Unavailable();
    }
    private static GraphicsReadbackResult Unavailable() => new(GraphicsReadbackOutcome.Unavailable, "Проверка UA Client Hints недоступна.");
    public static GraphicsReadbackResult ReadResult(string? json, string? expectedUserAgent = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "null");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) || status.GetString() != "Observed"
                || !root.TryGetProperty("secureContext", out var secure) || secure.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("userAgent", out var ua) || ua.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ua.GetString())) return Unavailable();
            var violation = new GraphicsReadbackResult(GraphicsReadbackOutcome.Violation, "UA Client Hints доступны или User-Agent отличается от заданного.");
            if (expectedUserAgent is not null && ua.GetString() != expectedUserAgent) return violation;
            if (!root.TryGetProperty("uaDataAvailable", out var available)) return Unavailable();
            var verified = new GraphicsReadbackResult(GraphicsReadbackOutcome.Verified, "UA Client Hints не раскрывают данные в этом контексте.");
            if (available.ValueKind == JsonValueKind.False) return verified;
            if (available.ValueKind != JsonValueKind.True || !root.TryGetProperty("lowEntropy", out var low) || low.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("highEntropy", out var high) || high.ValueKind != JsonValueKind.Object) return Unavailable();
            if (!low.TryGetProperty("brands", out var brands) || brands.ValueKind != JsonValueKind.Array
                || !low.TryGetProperty("platform", out var platform) || platform.ValueKind != JsonValueKind.String
                || !low.TryGetProperty("mobile", out var mobile) || mobile.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Unavailable();
            if (brands.GetArrayLength() != 0 || platform.GetString() != "" || mobile.GetBoolean()) return violation;
            foreach (var key in new[] {"architecture","bitness","model","platformVersion","uaFullVersion","platform"})
                if (high.TryGetProperty(key, out var v)) { if (v.ValueKind != JsonValueKind.String) return Unavailable(); if (v.GetString() != "") return violation; }
            foreach (var key in new[] {"brands","fullVersionList","formFactors"})
                if (high.TryGetProperty(key, out var v)) { if (v.ValueKind != JsonValueKind.Array) return Unavailable(); if (v.GetArrayLength() != 0) return violation; }
            foreach (var key in new[] {"wow64","mobile"})
                if (high.TryGetProperty(key, out var v)) { if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Unavailable(); if (v.GetBoolean()) return violation; }
            return verified;
        }
        catch (JsonException) { return Unavailable(); }
        catch (InvalidOperationException) { return Unavailable(); }
    }
}
