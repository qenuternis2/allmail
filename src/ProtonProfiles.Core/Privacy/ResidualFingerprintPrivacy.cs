using System.Text.Json;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Explicit script restrictions for APIs without native WebView2 switches. Not an undetectable fingerprint.</summary>
public static class ResidualFingerprintPrivacy
{
    public static bool IsEnabled(GraphicsPolicy policy) => AdditionalFingerprintPrivacy.IsEnabled(policy);
    private static string Read(string name)
    {
        using var stream=typeof(ResidualFingerprintPrivacy).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy."+name)
            ?? throw new InvalidOperationException("Residual privacy resource missing.");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    private static readonly Lazy<string> Guard=new(()=>Read("residual-fingerprint-guard.v1.js"));
    private static readonly Lazy<string> Observer=new(()=>Read("residual-fingerprint-observation.v1.js"));
    public static string Script=>Guard.Value;
    public static string ObservationScript=>Observer.Value;
    public static string EvaluationScript=>"(() => {\n"+ObservationScript+"\nreturn collectResidualFingerprintObservation();\n})()";
    public static GraphicsReadbackResult ReadResult(string? json)
    {
        var unavailable=new GraphicsReadbackResult(GraphicsReadbackOutcome.Unavailable,"Проверка программных ограничений не выполнена.");
        try
        {
            using var doc=JsonDocument.Parse(json??"null");var o=doc.RootElement;
            if(o.GetProperty("status").GetString()!="Observed")return unavailable;
            var good=o.GetProperty("deviceMemory").GetDouble()==8 && o.GetProperty("scriptRestriction").ValueKind==JsonValueKind.True;
            foreach(var group in new[]{"navigatorApis","constructors"})
            {
                var entries=o.GetProperty(group).EnumerateObject().ToArray();
                if(entries.Length!=(group=="navigatorApis"?5:19))return unavailable;
                foreach(var field in entries)
                {
                    if(field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                    good &= field.Value.ValueKind==JsonValueKind.False;
                }
            }
            foreach(var key in new[]{"performanceMemoryAvailable","storageEstimateAvailable"})
            {
                if(o.GetProperty(key).ValueKind is not (JsonValueKind.True or JsonValueKind.False))return unavailable;
                good &= o.GetProperty(key).ValueKind==JsonValueKind.False;
            }
            var font=o.GetProperty("localFontConstructionBlocked");
            if(font.ValueKind!=JsonValueKind.Null && font.ValueKind!=JsonValueKind.True && font.ValueKind!=JsonValueKind.False)return unavailable;
            good &= font.ValueKind is JsonValueKind.Null or JsonValueKind.True;
            return good?new(GraphicsReadbackOutcome.Verified,"RAM bucket 8 и программные ограничения API подтверждены; изменения JavaScript обнаружимы.")
                :new(GraphicsReadbackOutcome.Violation,"Программные ограничения RAM, устройств или шрифтов не подтверждены.");
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return unavailable;}
    }
}
