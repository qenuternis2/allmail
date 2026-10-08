using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Storage;

/// <summary>Observe an explicitly requested public EXE download; never execute it or approve a safety warning.</summary>
internal static class DownloadDiagnosticsSmoke
{
    private const string Url = "https://github.com/kurasis/FindCopy/releases/download/v1.0.1/FindCopy-win-x64.exe";
    private const long Size = 73825766;
    private const string Hash = "6aacb50429b3f5eef2da8a9a735e9f5384c0934a2d40504358af8713cbe7afba";
    public static async Task RunAsync(Window window, string root)
    {
        window.Left = 0; window.Top = 0; window.Width = 980; window.Height = 700;
        Directory.CreateDirectory("artifacts/test-results");
        var results = new List<object>();
        foreach (var mode in new[] { (Handled: true, Popup: false), (Handled: true, Popup: true), (Handled: false, Popup: true) })
        {
            var handled = mode.Handled;
            CoreWebView2DownloadOperation? operation = null;
            CoreWebView2? owner = null;
            string? protocol = null; var opened = false;
            string? beginUri = null; Guid? beginId = null;
            var controllers = 0; var operationController = 0; var protocolController = 0; var beginController = 0;
            var paths = new ManagedPaths(Path.Combine(root, "download-public-exe-" + handled + "-" + mode.Popup)); paths.EnsureBaseDirectories();
            var repository = new SqliteProfileRepository(paths.DatabasePath);
            var host = new DownloadsSmoke.Host(window, paths.Root) { ConfigureDownloadDiagnostics = core =>
            {
                var controller = ++controllers;
                core.GetDevToolsProtocolEventReceiver("Page.downloadProgress").DevToolsProtocolEventReceived += (_, e) => { protocol = e.ParameterObjectAsJson; protocolController = controller; };
                core.GetDevToolsProtocolEventReceiver("Page.downloadWillBegin").DevToolsProtocolEventReceived += (_, e) =>
                {
                    using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
                    beginUri = data.RootElement.GetProperty("url").GetString();
                    beginId = Guid.Parse(data.RootElement.GetProperty("guid").GetString()!); beginController = controller;
                };
                core.IsDefaultDownloadDialogOpenChanged += (_, _) => opened |= core.IsDefaultDownloadDialogOpen;
                core.DownloadStarting += async (_, e) =>
                {
                    var deferral = e.GetDeferral();
                    try { operation = e.DownloadOperation; owner = core; operationController = controller; await Task.Yield(); e.Handled = handled; }
                    finally { deferral.Complete(); }
                };
            }};
            var profile = ProfileStartPage.WithUrl(new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Public EXE diagnostics",
                TrackingPreventionLevel = TrackingPreventionLevel.Strict,
                GraphicsPolicy = GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechUaHintsFontAccessCpuDevicesAndPressureExperimental },
                "https://github.com/kurasis/FindCopy/releases/tag/v1.0.1");
            repository.Insert(profile);
            var context = new GenerationContext(profile.Id, 1); host.Current.Add(context);
            var engine = new WebView2Engine(host, paths, new PermissionPolicy(repository), new NavigationPolicy(), new InMemoryCredentialStore());
            var session = (WebView2Session)await engine.StartAsync(new(context, profile, 1, paths.UserDataFolder(profile.Id), host.Current.Contains), CancellationToken.None);
            try
            {
                var core = session.MainView!.CoreWebView2;
                await Task.Delay(2000);
                await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new {
                    expression = "(()=>{let a=document.createElement('a');a.href=" + JsonSerializer.Serialize(Url) + ";a.target='" + (mode.Popup ? "_blank" : "_self") + "';document.body.append(a);a.click();return true})()", userGesture = true }));
                var deadline = DateTime.UtcNow.AddSeconds(75); DateTime? receivedAt = null;
                while (DateTime.UtcNow < deadline)
                {
                    if (operation is not null)
                    {
                        if (operation.State != CoreWebView2DownloadState.InProgress) break;
                        if (operation.BytesReceived >= Size) receivedAt ??= DateTime.UtcNow;
                        if (receivedAt is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(15)) break;
                    }
                    await Task.Delay(100);
                }
                if (operation is null) throw new InvalidOperationException("Public EXE diagnostic did not receive DownloadStarting.");
                var item = host.Panel.Items(profile.Id).Single();
                var before = new { state = operation.State.ToString(), reason = operation.InterruptReason.ToString(), operation.BytesReceived,
                    operation.TotalBytesToReceive, appPhase = item.Info.Phase.ToString(), protocol, nativeDialogObserved = opened };
                // Native UI is read-only here: no Keep/Open/Run action is ever selected for this file.
                owner!.OpenDefaultDownloadDialog(); await Task.Delay(1500);
                var hwnd = new WindowInteropHelper(window).Handle;
                string[] names;
                try { names = await Task.Run(() =>
                {
                    var controls = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    return controls.Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => !string.IsNullOrWhiteSpace(n)
                        && new[] { "download", "FindCopy", "Keep", "Delete", "Remove", "danger", "unsafe", "common", "Save" }.Any(word => n.Contains(word, StringComparison.OrdinalIgnoreCase)))
                        .Distinct().Take(120).Select(n => n.Length <= 160 ? n : n[..160]).ToArray();
                }).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception e) when (e is TimeoutException or ElementNotAvailableException) { names = [e.GetType().Name]; }
                string? sha256 = null;
                string? fileError = null;
                if (File.Exists(item.FilePath))
                {
                    try { sha256 = await Task.Run(() => { using var input = new FileStream(item.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { fileError = e.GetType().Name; }
                    if (operation.State == CoreWebView2DownloadState.Completed && sha256 != Hash) throw new InvalidOperationException("Public EXE payload hash mismatch.");
                }
                var result = new { handled, popup = mode.Popup, tracking = profile.TrackingPreventionLevel.ToString(), graphics = (int)profile.GraphicsPolicy,
                    before, after = operation.State.ToString(), dialogOpen = owner.IsDefaultDownloadDialogOpen, names,
                    operationController, protocolController, beginController, sameUri = beginUri == operation.Uri, identified = beginId == item.Id,
                    nativeUriHost = new Uri(operation.Uri).Host, protocolUriHost = beginUri is null ? null : new Uri(beginUri).Host,
                    fileExists = File.Exists(item.FilePath), sha256, fileError, expectedHashMatch = sha256 == Hash, executed = false, safetyApproved = false };
                results.Add(result);
                File.WriteAllText("artifacts/test-results/download-public-exe.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("EXE DIAGNOSTIC: " + JsonSerializer.Serialize(result));
            }
            finally { await session.CloseAsync(); await session.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); host.Current.Remove(context); }
        }
    }
}
