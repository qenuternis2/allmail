using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Canonical V8 pow path only; no suppression of other engine or timing fingerprints.</summary>
public static class MathImplementationPrivacy
{
    private static readonly string[] ReferenceBits = ["3fcc8576b9821290","45c51f96ddfe1294","4067d365369167d6","408967e63d6967c0","3f54ba2f365f4928","3cf50efe4c0f072a","3fdb66f934e8c7a4","405578cdac450aa4","3ff3dd7d668fcde4","3fee369efcbec66e","40e70b81d193cd0e","4012665d63588a80","4008e5671ac17260","3f04e38b44ae1118","3f6df8f995c43c2c","3ffa80e859564e22"];
    public static bool IsEnabled(ProfileConfig config)=>config.GraphicsPolicy==GraphicsPolicy.StrictFingerprintExperimental&&!ProfilePrivacy.Allows(config,PrivacyException.NativeMath);
    public static string EvaluationScript=>"(() => {\n"+ResidualFingerprintPrivacy.ObservationScript+"\nreturn collectMathPowObservation();\n})()";
    public static GraphicsReadbackOutcome ReadResult(string? json)
    {
        try {
            using var doc=JsonDocument.Parse(json??"null");var o=doc.RootElement;
            if(o.GetProperty("status").GetString()!="Observed"||o.GetProperty("vectors").GetInt32()!=16)return GraphicsReadbackOutcome.Unavailable;
            var values=o.GetProperty("values");
            if(values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()!=16||values.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String||v.GetString() is not {Length:16} text||text.Any(c=>!Uri.IsHexDigit(c))))return GraphicsReadbackOutcome.Unavailable;
            foreach(var key in new[]{"native","referenceMatches"})if(o.GetProperty(key).ValueKind is not (JsonValueKind.True or JsonValueKind.False))return GraphicsReadbackOutcome.Unavailable;
            return o.GetProperty("native").GetBoolean()&&o.GetProperty("referenceMatches").GetBoolean()&&values.EnumerateArray().Select(v=>v.GetString()).SequenceEqual(ReferenceBits)?GraphicsReadbackOutcome.Verified:GraphicsReadbackOutcome.Violation;
        }catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return GraphicsReadbackOutcome.Unavailable;}
    }
}
