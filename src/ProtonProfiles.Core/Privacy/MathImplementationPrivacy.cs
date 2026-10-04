using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Canonical V8 pow path only; no suppression of other engine or timing fingerprints.</summary>
public static class MathImplementationPrivacy
{
    private static readonly string[] ReferenceBits = ["3fef967b5aa8b974","40cf3286f4ca2958","405f5407e07e0932","4162f2ad4c9e14d6","3eafc314ad19faa7","401fc2b51a4228db","3fa5421457a49441","3f1631c56724ff44","42ad93a9ee2439f0","3fa3bbc8df1da607","414b36ac8da32e9c","3fc387cdcec1b63a","40d4575be716f225","411167c2916e2fba","3fcee5f22ebb0553","3e8901e1f9db84cd"];
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
