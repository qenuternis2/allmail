using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Data.Sqlite;
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
        window.Left = 0; window.Top = 0; window.Width = 980; window.Height = 700; window.Topmost = true;
        Directory.CreateDirectory("artifacts/test-results");
        var results = new List<object>();
        var modes = new[] { (Handled: true, Popup: false, ReputationChecking: true),
            (Handled: false, Popup: false, ReputationChecking: true), (Handled: false, Popup: false, ReputationChecking: false) };
        foreach (var mode in modes)
        {
            var handled = mode.Handled;
            CoreWebView2DownloadOperation? operation = null;
            CoreWebView2? owner = null;
            string? protocol = null; var opened = false;
            string? beginUri = null; Guid? beginId = null;
            bool? effectiveHandled = null;
            var controllers = 0; var operationController = 0; var protocolController = 0; var beginController = 0;
            var label = handled + "-" + mode.Popup + "-" + mode.ReputationChecking;
            var paths = new ManagedPaths(Path.Combine(root, "download-public-exe-" + label)); paths.EnsureBaseDirectories();
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
                    try
                    {
                        operation = e.DownloadOperation; owner = core; operationController = controller; await Task.Yield();
                        // Reproduce the old hidden mode explicitly. The other case uses
                        // the production handler unchanged, so a regression can fail it.
                        if (handled) e.Handled = true;
                        effectiveHandled = e.Handled;
                    }
                    finally { deferral.Complete(); }
                };
            }};
            var profile = ProfileStartPage.WithUrl(new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Public EXE diagnostics",
                TrackingPreventionLevel = TrackingPreventionLevel.Strict,
                ReputationCheckingEnabled = mode.ReputationChecking,
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
                        var observation = Environment.GetEnvironmentVariable("ALLMAIL_DOWNLOAD_DIAGNOSTICS_DEEP") == "1" ? 35 : 15;
                        if (receivedAt is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(observation)) break;
                    }
                    await Task.Delay(100);
                }
                if (operation is null) throw new InvalidOperationException("Public EXE diagnostic did not receive DownloadStarting.");
                var item = host.Panel.Items(profile.Id).Single();
                var before = new { state = operation.State.ToString(), reason = operation.InterruptReason.ToString(), operation.BytesReceived,
                    operation.TotalBytesToReceive, appPhase = item.Info.Phase.ToString(), protocol, nativeDialogObserved = opened };
                // Native UI is read-only here: no Keep/Open/Run action is ever selected for this file.
                owner!.OpenDefaultDownloadDialog(); await Task.Delay(1500);
                var nativeWindows = await NativeDownloadUi.ObserveAsync(window, session.Environment, "public-exe-" + label);
                var listed = nativeWindows.Any(w => w.Visible && w.Names.Contains("Downloads")
                    && w.Names.Any(n => n.Contains("FindCopy-win-x64.exe", StringComparison.Ordinal)));
                var targets = await owner.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}");
                var hub = await NativeDownloadUi.InspectHubAsync(session);
                // UIA activates the native renderer's accessibility tree asynchronously.
                // Confirm its browser-owned WebUI too, rather than interpreting an
                // initially unavailable AX subtree as an empty download list.
                var nodes = hub.Dom.GetProperty("result").GetProperty("value").EnumerateArray().ToArray();
                listed |= nodes.Any(n => n.GetProperty("tag").GetString() == "DOWNLOAD-ITEM"
                    && n.GetProperty("label").GetString()!.Contains("FindCopy-win-x64.exe", StringComparison.Ordinal)
                    && n.GetProperty("visible").GetBoolean());
                var keepAvailable = nodes.Any(n => n.GetProperty("visible").GetBoolean()
                    && (n.GetProperty("action").GetString() == "keepDangerous" || n.GetProperty("text").GetString() == "Keep"))
                    || hub.Windows.Any(w => w.Visible && w.Names.Contains("Keep"));
                if (handled == listed || effectiveHandled != handled)
                    throw new InvalidOperationException("Native download entry visibility does not match the production/hidden mode.");
                var history = ReadFixtureHistory(paths.UserDataFolder(profile.Id));
                var ownedProcesses = session.Environment.GetProcessInfos().Select(p => p.ProcessId).Append(Environment.ProcessId).Distinct().ToArray();
                string[] names;
                try { names = await Task.Run(() =>
                {
                    var condition = new OrCondition(ownedProcesses.Select(id => new PropertyCondition(AutomationElement.ProcessIdProperty, id)).ToArray());
                    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, condition).Cast<AutomationElement>();
                    return windows.SelectMany(w => w.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>())
                        .Select(e => e.Current.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(250)
                        .Select(n => n.Length <= 160 ? n : n[..160]).ToArray();
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
                var fileTypeBlocked = nodes.Any(n => n.GetProperty("text").GetString()!.Contains("because this type of file can harm", StringComparison.Ordinal))
                    || nativeWindows.Any(w => w.Names.Any(n => n.Contains("because this type of file can harm", StringComparison.Ordinal)));
                var uncommonWarning = nodes.Any(n => n.GetProperty("text").GetString()!.Contains("isn't commonly downloaded", StringComparison.Ordinal))
                    || nativeWindows.Any(w => w.Names.Any(n => n.Contains("isn't commonly downloaded", StringComparison.Ordinal)));
                var result = new { handled, effectiveHandled, listed, keepAvailable, popup = mode.Popup,
                    reputationCheckingEnabled = profile.ReputationCheckingEnabled, nativeReputationCheckingRequired = owner.Settings.IsReputationCheckingRequired,
                    fileTypeBlocked, uncommonWarning,
                    tracking = profile.TrackingPreventionLevel.ToString(), graphics = (int)profile.GraphicsPolicy,
                    before, after = operation.State.ToString(), dialogOpen = owner.IsDefaultDownloadDialogOpen, nativeWindows, targets, hub, names, history,
                    operationController, protocolController, beginController, sameUri = beginUri == operation.Uri, identified = beginId == item.Id,
                    nativeUriHost = new Uri(operation.Uri).Host, protocolUriHost = beginUri is null ? null : new Uri(beginUri).Host,
                    fileExists = File.Exists(item.FilePath), sha256, fileError, expectedHashMatch = sha256 == Hash, executed = false, safetyApproved = false };
                results.Add(result);
                File.WriteAllText("artifacts/test-results/download-public-exe.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("EXE DIAGNOSTIC: " + JsonSerializer.Serialize(result));
                // SmartScreen off does not bypass Chromium's independent dangerous-file-type gate.
                // Verify the reputation warning disappeared, not a promised automatic EXE completion.
                if (!mode.ReputationChecking && (owner.Settings.IsReputationCheckingRequired || sha256 != Hash || uncommonWarning
                    || !(fileTypeBlocked && keepAvailable || operation.State == CoreWebView2DownloadState.Completed && item.Info.Phase == DownloadPhase.Completed)))
                    throw new InvalidOperationException("Disabled SmartScreen did not remove the reputation warning or preserve explicit file-type review.");
            }
            finally
            {
                await session.CloseAsync(); await session.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); host.Current.Remove(context);
                // Chromium holds History exclusively. Inspect only after the fixture process exits;
                // shutdown may change state/interrupt_reason, so never treat these as live values.
                File.WriteAllText("artifacts/test-results/download-history-after-close-" + label + ".json",
                    JsonSerializer.Serialize(ReadFixtureHistory(paths.UserDataFolder(profile.Id)), new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
    private static object ReadFixtureHistory(string userDataFolder)
    {
        // Only this disposable fixture's History is inspected. No application/user profile is read.
        var path = Path.Combine(userDataFolder, "EBWebView", "Default", "History");
        if (!File.Exists(path)) return new { error = "Fixture History absent" };
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ConnectionString);
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT guid, state, danger_type, interrupt_reason, received_bytes, total_bytes FROM downloads ORDER BY start_time DESC LIMIT 5";
            using var rows = command.ExecuteReader(); var downloads = new List<Dictionary<string, object?>>();
            while (rows.Read()) downloads.Add(Enumerable.Range(0, rows.FieldCount).ToDictionary(rows.GetName, i => rows.IsDBNull(i) ? null : rows.GetValue(i)));
            return downloads;
        }
        catch (SqliteException e) { return new { error = e.SqliteErrorCode }; }
    }
}
