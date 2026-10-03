using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProtonProfiles.Core.Diagnostics;

/// <summary>Scrubs known network fields from the fingerprint collector; never edits the live local report.</summary>
public static class FingerprintReportExport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Prepare(string reportJson, bool hideNetworkAddresses = true)
    {
        var root = JsonNode.Parse(reportJson) as JsonObject ?? throw new JsonException("Ожидается объект отчёта.");
        var report = root["partial"] as JsonObject ?? root;
        if (hideNetworkAddresses)
        {
            if (report["sections"]?["Сеть"] is JsonObject network)
                foreach (var key in new[] { "IP (ipinfo.io)", "IPv4 (ipify)", "IPv6 (ipify)", "Хост" })
                    if (network.ContainsKey(key)) network[key] = "[скрыто]";
            if (report["sections"]?["Заголовки, которые видит сервер (httpbin.org)"] is JsonObject headers)
                foreach (var key in headers.Select(p => p.Key).ToArray())
                    if (key.Contains("forwarded", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith("-ip", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("True-Client-IP", StringComparison.OrdinalIgnoreCase)) headers[key] = "[скрыто]";
            if (report["networkObservations"] is JsonObject observations)
                foreach (var observation in observations.Select(p => p.Value).OfType<JsonObject>())
                    if (observation.ContainsKey("address")) observation["address"] = "[скрыто]";
        }
        report["networkAddressesHidden"] = hideNetworkAddresses;
        return root.ToJsonString(Json);
    }
}
