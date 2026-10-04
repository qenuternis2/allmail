using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ProtonProfiles.App.Browser;

/// <summary>Serves the embedded collector without disk copies or persistent browser cache.</summary>
internal static class FingerprintProbePage
{
    public const string Host = "diagnostics.invalid";
    public const int ReportVersion = 16;
    public static string ApplicationVersion => typeof(FingerprintProbePage).Assembly.GetName().Version!.ToString(3);
    private static readonly Lazy<byte[]> Content = new(() => {
        using var stream = typeof(FingerprintProbePage).Assembly.GetManifestResourceStream("ProtonProfiles.App.Diagnostics.fingerprint.html")
            ?? throw new InvalidOperationException("Страница проверки не найдена в сборке.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    });
    public static string CollectorHash => Convert.ToHexStringLower(SHA256.HashData(Content.Value));
    public static string NavigationUri => $"https://{Host}/fingerprint.html?build={ApplicationVersion}&collector={CollectorHash}&run={Guid.NewGuid():N}";
    public static bool IsPageUri(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.Host == Host && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/fingerprint.html";

    public static async Task ConfigureAsync(CoreWebView2 core, CoreWebView2Environment environment)
    {
        // Share the strict-mode interceptor: two Fetch.requestPaused handlers would
        // race to continue a request. Other modes keep all their client hints.
        var requests=ClientHintsRequests.ForCore(core,()=>{
            try{return core.BrowserProcessId>0;}catch{return false;}
        },null,stripClientHints:false);
        requests.EnableCollector();
        await requests.ConfigureAsync();
        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) => {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Host != Host)
                return;
            var page = IsPageUri(e.Request.Uri) && e.Request.Method == "GET" && e.ResourceContext == CoreWebView2WebResourceContext.Document;
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(page ? Content.Value : []), page ? 200 : 404,
                page ? "OK" : "Not Found", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store, max-age=0\r\nPragma: no-cache\r\nReferrer-Policy: no-referrer\r\n");
        };
    }

    public static bool IsCurrentReport(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("reportVersion", out var schema) && schema.TryGetInt32(out var version) && version == ReportVersion
                && root.TryGetProperty("applicationVersion", out var build) && build.ValueKind == JsonValueKind.String && build.GetString() == ApplicationVersion
                && root.TryGetProperty("collectorHash", out var hash) && hash.ValueKind == JsonValueKind.String && hash.GetString() == CollectorHash;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
