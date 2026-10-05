using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

/// <summary>Anonymous page observation only: no input, form submission or challenge interaction.</summary>
internal static class CloudflareCompatibilityDiagnostics
{
    private const string Url = "https://dash.cloudflare.com/login";
    private const string Snapshot = """
        (() => ({title:document.title, host:location.hostname, path:location.pathname,
          text:document.body?.innerText?.slice(0,1800),
          buttons:[...document.querySelectorAll('button')].map(b=>({text:b.innerText?.slice(0,100),disabled:b.disabled,type:b.type})),
          inputs:[...document.querySelectorAll('input')].map(i=>({type:i.type,name:i.name})),
          frames:[...document.querySelectorAll('iframe')].map(f=>{try{return new URL(f.src).hostname}catch{return ''}}),
          features:{crypto:!!crypto.subtle, fontsCheck:typeof document.fonts?.check,
            measureText:typeof CanvasRenderingContext2D.prototype.measureText, serviceWorker:typeof navigator.serviceWorker},
          turnstileAvailable:typeof globalThis.turnstile !== 'undefined'}))()
        """;

    public static async Task RunAsync(Window window, string root)
    {
        window.Width = 1100; window.Height = 900;
        var cases = new[] {
            ("default-balanced", GraphicsPolicy.RuntimeDefault, TrackingPreventionLevel.Balanced, PrivacyException.None),
            ("default-strict-tracking", GraphicsPolicy.RuntimeDefault, TrackingPreventionLevel.Strict, PrivacyException.None),
            ("strict", GraphicsPolicy.StrictFingerprintExperimental, TrackingPreventionLevel.Strict, PrivacyException.None),
            ("strict-balanced-tracking", GraphicsPolicy.StrictFingerprintExperimental, TrackingPreventionLevel.Balanced, PrivacyException.None),
            ("strict-timers", GraphicsPolicy.StrictFingerprintExperimental, TrackingPreventionLevel.Strict, PrivacyException.HighResolutionTimers),
            ("strict-fonts", GraphicsPolicy.StrictFingerprintExperimental, TrackingPreventionLevel.Strict, PrivacyException.LocalFonts),
            ("strict-text-metrics", GraphicsPolicy.StrictFingerprintExperimental, TrackingPreventionLevel.Strict, PrivacyException.CanvasTextMetrics),
            ("strict-all-exceptions", GraphicsPolicy.StrictFingerprintExperimental, TrackingPreventionLevel.Strict, ProfilePrivacy.KnownExceptions),
        };
        foreach (var (label, graphics, tracking, exceptions) in cases)
        {
            var paths = new ManagedPaths(Path.Combine(root, label));
            paths.EnsureBaseDirectories();
            var repository = new SqliteProfileRepository(paths.DatabasePath);
            var host = new Host(window);
            var engine = new WebView2Engine(host, paths, new PermissionPolicy(repository), new NavigationPolicy(), new InMemoryCredentialStore());
            var config = new ProfileConfig { Id=Guid.NewGuid(), DisplayName="Public Cloudflare diagnostic", Kind=ProfileKind.Test,
                TestStartUrl=Url, GraphicsPolicy=graphics, TrackingPreventionLevel=tracking, PrivacyExceptions=exceptions, BrowserTimeZoneId="Europe/London" };
            var context = new GenerationContext(config.Id, 1);
            try
            {
                host.Session = (WebView2Session)await engine.StartAsync(new(context, config, 1, paths.UserDataFolder(config.Id), _=>host.Current), CancellationToken.None);
                var core = host.Session.MainView!.CoreWebView2;
                await Task.Delay(12000);
                using var result = JsonDocument.Parse(await core.ExecuteScriptAsync(Snapshot));
                Console.WriteLine("PUBLIC_CLOUDFLARE_DIAGNOSTIC " + JsonSerializer.Serialize(new {
                    label, graphics, tracking, exceptions=ProfilePrivacy.Names(exceptions), status="Observed",
                    snapshot=result.RootElement.Clone(), errors=host.Errors, failedResources=host.FailedResources, problems=host.Problems,
                }));
                Directory.CreateDirectory("artifacts/test-results");
                await using var image = File.Create("artifacts/test-results/cloudflare-" + label + ".png");
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            catch (Exception e)
            {
                Console.WriteLine("PUBLIC_CLOUDFLARE_DIAGNOSTIC " + JsonSerializer.Serialize(new {
                    label, status="NotPerformed", errorName=e.GetType().Name, error=Clean(e.Message), problems=host.Problems,
                }));
            }
            finally
            {
                host.Current = false;
                if (host.Session is { } session) { await session.CloseAsync(); await session.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); }
                window.Content = null;
            }
        }
    }

    private static string Clean(string text)
    {
        var cleaned = Regex.Replace(text, @"https?://\S+", "[url]");
        return cleaned[..Math.Min(600, cleaned.Length)];
    }

    private sealed class Host : IBrowserViewHost
    {
        private readonly Grid _pages = new();
        private WebView2? _active;
        public bool Current { get; set; } = true;
        public WebView2Session? Session { get; set; }
        public List<string> Errors { get; } = [];
        public List<object> FailedResources { get; } = [];
        public List<string> Problems { get; } = [];
        public Host(Window window) => window.Content = _pages;
        public void Attach(GenerationContext context, WebView2 view)
        {
            _pages.Children.Add(view); view.Visibility=Visibility.Hidden;
            view.CoreWebView2InitializationCompleted += async (_, e) => {
                if (!e.IsSuccess) return;
                var core = view.CoreWebView2;
                core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, exception) => {
                    if (Errors.Count >= 12) return;
                    using var doc = JsonDocument.Parse(exception.ParameterObjectAsJson);
                    var details = doc.RootElement.GetProperty("exceptionDetails");
                    var text = details.TryGetProperty("exception", out var value) && value.TryGetProperty("description", out var description)
                        ? description.GetString() : details.GetProperty("text").GetString();
                    Errors.Add(Clean(text ?? ""));
                };
                core.WebResourceResponseReceived += (_, response) => {
                    if (response.Response.StatusCode < 400 || FailedResources.Count >= 20 || !Uri.TryCreate(response.Request.Uri, UriKind.Absolute, out var uri)) return;
                    FailedResources.Add(new { host=uri.Host, path=uri.AbsolutePath, status=response.Response.StatusCode });
                };
                core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, console) => {
                    if (Errors.Count >= 12) return;
                    using var doc = JsonDocument.Parse(console.ParameterObjectAsJson);
                    var entry = doc.RootElement;
                    if (entry.GetProperty("type").GetString() is not ("error" or "warning")) return;
                    foreach (var argument in entry.GetProperty("args").EnumerateArray().Take(3))
                        if (Errors.Count < 12) Errors.Add(Clean(argument.TryGetProperty("value",out var value) ? value.ToString()
                            : argument.TryGetProperty("description",out var description) ? description.GetString() ?? "" : ""));
                };
                try { await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}"); }
                catch (Exception error) { Problems.Add("Diagnostic observer unavailable: " + error.GetType().Name); }
            };
        }
        public void Detach(GenerationContext context, WebView2 view) { _pages.Children.Remove(view); if (_active==view) _active=null; }
        public void TabReady(GenerationContext context, WebView2 view) => SelectTab(context, view);
        public void SelectTab(GenerationContext context, WebView2 view) { foreach (WebView2 page in _pages.Children) page.Visibility=page==view?Visibility.Visible:Visibility.Hidden; _active=view; }
        public WebView2? ActiveView(GenerationContext context) => _active;
        public Task<UserPermissionAnswer?> AskPermissionAsync(GenerationContext context, string origin, PermissionKindKey kind) => Task.FromResult<UserPermissionAnswer?>(null);
        public void OfferExternalLink(GenerationContext context, string uri) { }
        public Task<string?> ChooseDownloadPathAsync(GenerationContext context, string sanitizedFileName, string? initialDirectory) => Task.FromResult<string?>(null);
        public void ReportDownload(DownloadInfo info) { }
        public void ReportProblem(GenerationContext context, string message) => Problems.Add(Clean(message));
        public async Task StopProfileAsync(GenerationContext context, string message) { Current=false; ReportProblem(context,message); if(Session is { } session)await session.CloseAsync(); }
    }
}
