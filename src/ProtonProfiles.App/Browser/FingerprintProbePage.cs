using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ProtonProfiles.App.Browser;

/// <summary>Serves the embedded collector without disk copies or persistent browser cache.</summary>
internal static class FingerprintProbePage
{
    public const string Host = "diagnostics.invalid";
    public const string ContextHost = "contexts.invalid";
    public const int ReportVersion = 22;
    public static string ApplicationVersion => typeof(FingerprintProbePage).Assembly.GetName().Version!.ToString(3);
    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(FingerprintProbePage).Assembly.GetManifestResourceStream("ProtonProfiles.App.Diagnostics." + name)
            ?? throw new InvalidOperationException("Страница проверки не найдена в сборке.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
    private static readonly Lazy<byte[]> Content = new(() => ReadResource("fingerprint.html"));
    private static readonly Lazy<byte[]> ContextContent = new(() => ReadResource("context.html"));
    private static readonly Lazy<byte[]> ObserverContent = new(() => ReadResource("context-observation.v1.js"));
    private static readonly Lazy<string> Hash = new(() => {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, content) in new[] {("fingerprint.html", Content.Value),
            ("context-observation.v1.js", ObserverContent.Value), ("context.html", ContextContent.Value)})
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name + "\0"));
            hash.AppendData(content);
            hash.AppendData(new byte[] {0});
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    });
    public static string CollectorHash => Hash.Value;
    public static string WebCryptoEvaluationScript=>"(async()=>{\n"+System.Text.Encoding.UTF8.GetString(ObserverContent.Value)+"\nreturn {webCrypto:await collectWebCryptoObservation(),javascriptIntrinsics:collectJavaScriptIntrinsicsObservation(),protonSupportedBrowser:typeof globalThis.protonSupportedBrowser==='number'?globalThis.protonSupportedBrowser:null,protonBrowserPrerequisites:{objectFromEntries:typeof Object.fromEntries==='function',trimStart:typeof ''.trimStart==='function',olReversed:typeof document==='object'&&'reversed' in document.createElement('ol')}};})()";
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
        await core.AddScriptToExecuteOnDocumentCreatedAsync(System.Text.Encoding.UTF8.GetString(ObserverContent.Value));
        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.AddWebResourceRequestedFilter($"https://{ContextHost}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) => {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Host != Host && uri.Host != ContextHost)
                return;
            var page = IsPageUri(e.Request.Uri) && e.Request.Method == "GET" && e.ResourceContext == CoreWebView2WebResourceContext.Document;
            var context = uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/context.html"
                && e.Request.Method == "GET" && e.ResourceContext == CoreWebView2WebResourceContext.Document;
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(page ? Content.Value : context ? ContextContent.Value : []), page || context ? 200 : 404,
                page || context ? "OK" : "Not Found", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store, max-age=0\r\nPragma: no-cache\r\nReferrer-Policy: no-referrer\r\n");
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
