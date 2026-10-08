using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.App.Controls;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Storage;

internal static class DownloadsSmoke
{
    public static async Task RunAsync(Window window, string root)
    {
        DownloadCompletionSmoke.Run(root);
        using var server = new Server();
        var paths = new ManagedPaths(Path.Combine(root, "download-status")); paths.EnsureBaseDirectories();
        var repository = new SqliteProfileRepository(paths.DatabasePath);
        var host = new Host(window, paths.Root);
        var engine = new WebView2Engine(host, paths, new PermissionPolicy(repository), new NavigationPolicy(), new InMemoryCredentialStore());
        var sessions = new List<WebView2Session>();
        async Task<WebView2Session> Start(Guid id, long generation)
        {
            var profile = ProfileStartPage.WithUrl(new ProfileConfig { Id = id, DisplayName = "Downloads fixture" }, server.Url + "/");
            if (repository.Get(id) is null) repository.Insert(profile);
            var context = new GenerationContext(id, generation); host.Current.Add(context);
            var session = (WebView2Session)await engine.StartAsync(new(context, profile, 1, paths.UserDataFolder(id), host.Current.Contains), CancellationToken.None);
            sessions.Add(session); if (generation > 1) session.MainView!.CoreWebView2.Navigate(server.Url + "/"); await Until(() => host.Loaded.Contains(session.MainView!)); return session;
        }
        window.Width = 960; window.Height = 600;
        try
        {
            var a = await Start(Guid.NewGuid(), 1);
            var origin = a.MainView!;
            var tab = await engine.OpenTabAsync(a, server.Url + "/") ?? throw new InvalidOperationException("download second tab");
            await Until(() => host.Loaded.Contains(tab));
            origin.CoreWebView2.Navigate(server.Url + "/known.bin");
            tab.CoreWebView2.Navigate(server.Url + "/unknown.bin");
            await Until(() => host.Panel.Items(a.Context.ProfileId).Count == 2 && host.Panel.Items(a.Context.ProfileId).All(i => i.Info.BytesReceived > 0));
            var known = host.Panel.Items(a.Context.ProfileId).Single(i => i.FileName == "known.bin");
            var unknown = host.Panel.Items(a.Context.ProfileId).Single(i => i.FileName == "unknown.bin");
            await Until(() => known.Info.BytesPerSecond is > 0 && known.Info.Remaining is not null);
            Require(known.Info.TotalBytes == Server.Size && unknown.Info.TotalBytes is null && unknown.Indeterminate, "known and unknown size, actual rate/ETA");
            Require(host.Panel.ActiveCount(a.Context.ProfileId) == 2 && host.Panel.Items(a.Context.ProfileId).Count == 2, "parallel progress doesn't inflate count");
            Action? staleCancel = known.Info.Cancel;
            Click(host.Panel, known, "Приостановить загрузку");
            await Until(() => known.Info.Phase == DownloadPhase.Paused && known.Info.Resume is not null);
            await Task.Delay(300); var pausedBytes = known.Info.BytesReceived; await Task.Delay(1100);
            Require(known.Info.BytesReceived == pausedBytes && known.Info.BytesPerSecond is null && host.Panel.ActiveCount(a.Context.ProfileId) == 2, "native pause stops progress without becoming cancelled");
            Click(host.Panel, unknown, "Отменить загрузку");
            await Until(() => unknown.Info.Phase == DownloadPhase.Cancelled && host.Panel.ActiveCount(a.Context.ProfileId) == 1);
            Capture(host.Panel, "downloads-paused");
            await a.CloseTabAsync(origin);
            Require(!a.Views.Contains(origin) && a.BackgroundDownloadViewCount == 1 && known.Info.Phase == DownloadPhase.Paused, "closed tab retains paused native controller");
            Click(host.Panel, known, "Продолжить загрузку");
            await Until(() => known.Info.Phase == DownloadPhase.Completed);
            Require(File.Exists(known.FilePath) && new FileInfo(known.FilePath!).Length == Server.Size && known.Percent == 100 && host.Panel.ActiveCount(a.Context.ProfileId) == 0, "completed file and accurate final state");
            var data = File.ReadAllBytes(known.FilePath!);
            Require(data.Select((value, index) => value == (byte)(index % 251)).All(value => value), "resume retained complete binary payload");
            await Until(() => host.ProtocolCompleted.TryGetValue(known.Id, out var completedBytes) && completedBytes == Server.Size);
            Console.WriteLine("PASS: passive Page.downloadProgress completed observed for actual GUID and verified resumed file; download behavior unchanged.");
            a.MainView!.CoreWebView2.Navigate(server.Url + "/broken.bin");
            await Until(() => host.Panel.Items(a.Context.ProfileId).Any(i => i.FileName == "broken.bin" && i.Info.Phase == DownloadPhase.Interrupted && i.Info.Resume is not null));
            var brokenItems = host.Panel.Items(a.Context.ProfileId).Where(i => i.FileName == "broken.bin").ToArray();
            Require(brokenItems.Length == 1, "one truncated download operation: " + string.Join("; ", brokenItems.Select(i => $"{i.Id} {i.Info.Phase} {i.Info.BytesReceived} {i.Reason}")));
            var broken = brokenItems.Single();
            var brokenView = a.MainView!;
            await a.CloseTabAsync(brokenView);
            Require(a.Views.Count == 1 && a.MainView!.CoreWebView2.Source == "about:blank" && a.BackgroundDownloadViewCount == 1 && !a.IsClosing, "last tab replaced with blank while resumable transfer remains");
            Require(!string.IsNullOrEmpty(broken.Reason) && broken.Info.Phase != DownloadPhase.Completed, "truncated response gives human error");
            Capture(host.Panel, "downloads-errors");
            // Pause can keep the existing socket open. A dropped response tests a real new Range request.
            server.RecoverBroken = true;
            Click(host.Panel, broken, "Продолжить загрузку");
            await Until(() => broken.Info.Phase == DownloadPhase.Completed);
            var recovered = File.ReadAllBytes(broken.FilePath!);
            Require(recovered.Length == Server.Size && recovered.Select((value, index) => value == (byte)(index % 251)).All(value => value), "interrupted resume retained complete binary payload");
            Require(server.Ranges > 0 && host.Panel.ActiveCount(a.Context.ProfileId) == 0, "real HTTP Range resume after interruption");
            Require(host.Panel.Items(a.Context.ProfileId).Count(i => i.FileName == "broken.bin") == 1 && host.SaveRequests.GetValueOrDefault("broken.bin") == 1, "native automatic/manual retries keep one record and one save dialog");
            await Until(() => a.BackgroundDownloadViewCount == 0);
            var repeatedView = a.MainView!;
            repeatedView.CoreWebView2.Navigate(server.Url + "/repeat.bin");
            await Until(() => host.Panel.Items(a.Context.ProfileId).Any(i => i.FileName == "repeat-1.bin" && i.Info.BytesReceived > 0));
            repeatedView.CoreWebView2.Navigate(server.Url + "/repeat.bin");
            await Until(() => host.Panel.Items(a.Context.ProfileId).Any(i => i.FileName == "repeat-2.bin" && i.Info.BytesReceived > 0));
            Require(host.Panel.ActiveCount(a.Context.ProfileId) == 2 && host.SaveRequests.GetValueOrDefault("repeat.bin") == 2, "fresh downloads of the same URL remain independent");
            foreach (var repeated in host.Panel.Items(a.Context.ProfileId).Where(i => i.FileName.StartsWith("repeat-", StringComparison.Ordinal))) Click(host.Panel, repeated, "Отменить загрузку");
            await Until(() => host.Panel.ActiveCount(a.Context.ProfileId) == 0);
            await Until(() => a.DownloadIdentityCount == 0 && a.DownloadChoiceCount == 0);
            Require(a.Views.Contains(repeatedView), "terminal bookkeeping released while its native tab remains open");
            var blobView = await engine.OpenTabAsync(a,server.Url+"/") ?? throw new InvalidOperationException("blob download tab");
            await Until(()=>host.Loaded.Contains(blobView));
            await blobView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",System.Text.Json.JsonSerializer.Serialize(new {
                expression="const bytes=Uint8Array.from({length:4096},(_,i)=>i%251);const blobUrl=URL.createObjectURL(new Blob([bytes]));const a=document.createElement('a');a.href=blobUrl;a.download='attachment-blob.bin';document.body.append(a);a.click();true",userGesture=true}));
            await Until(()=>host.Panel.Items(a.Context.ProfileId).Any(i=>i.FileName=="attachment-blob.bin"&&i.Info.Phase==DownloadPhase.Completed));
            var blobItem=host.Panel.Items(a.Context.ProfileId).Single(i=>i.FileName=="attachment-blob.bin");
            var blobBytes=File.ReadAllBytes(blobItem.FilePath!);
            Require(blobBytes.Length==4096&&blobBytes.Select((v,i)=>v==(byte)(i%251)).All(v=>v),"blob attachment owning-browser path and complete payload");
            await Until(() => a.DownloadIdentityCount == 0 && a.DownloadChoiceCount == 0);
            Console.WriteLine("PASS: production blob attachment download: native browser operation, owned profile, completed status and exact 4096-byte payload.");
            var b = await Start(Guid.NewGuid(), 1);
            var bOrigin = b.MainView!;
            bOrigin.CoreWebView2.Navigate(server.Url + "/other.bin");
            await Until(() => host.Panel.Items(b.Context.ProfileId).Any(i => i.Info.BytesReceived > 0));
            Require(host.Panel.ActiveCount(b.Context.ProfileId) == 1 && !VisibleNames(host.Panel).Contains("known.bin"), "profile download lists isolated");
            // Cancelling a save dialog must not decrement another active operation.
            var extra = await engine.OpenTabAsync(b, server.Url + "/") ?? throw new InvalidOperationException("save cancel tab");
            extra.CoreWebView2.Navigate(server.Url + "/save-cancel.bin");
            await Until(() => host.Panel.Items(b.Context.ProfileId).Any(i => i.FileName == "save-cancel.bin" && i.Info.Phase == DownloadPhase.Cancelled));
            Require(host.Panel.ActiveCount(b.Context.ProfileId) == 1, "save-dialog cancellation preserves other operation count");
            await Until(() => b.DownloadChoiceCount == 1 && b.DownloadIdentityCount == 1);
            var bDownload = host.Panel.Items(b.Context.ProfileId).Single(i => i.FileName == "other.bin");
            await b.CloseTabAsync(bOrigin);
            Require(bDownload.Info.Phase == DownloadPhase.InProgress && b.BackgroundDownloadViewCount == 1, "tab close keeps active download running");
            await Until(() => bDownload.Info.Phase == DownloadPhase.Completed && b.BackgroundDownloadViewCount == 0);
            await Until(() => b.DownloadChoiceCount == 0 && b.DownloadIdentityCount == 0);
            Require(File.ReadAllBytes(bDownload.FilePath!).Select((value, index) => value == (byte)(index % 251)).All(value => value)
                && new FileInfo(bDownload.FilePath!).Length == Server.Size, "closed-tab download complete payload and controller released");
            // Real website popup closes itself while DownloadStarting is awaiting a save choice.
            await extra.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new { expression = "window.open('" + server.Url + "/','_blank');true", userGesture = true }));
            await Until(() => b.Views.Count == 2 && !ReferenceEquals(b.MainView, extra) && host.Loaded.Contains(b.MainView!));
            var popup = b.MainView!;
            await popup.CoreWebView2.ExecuteScriptAsync("location.href='" + server.Url + "/auto-close.bin';setTimeout(()=>window.close(),250);true");
            await Until(() => !b.Views.Contains(popup));
            await Until(() => host.Panel.Items(b.Context.ProfileId).Any(i => i.FileName == "auto-close.bin" && i.Info.BytesReceived > 0));
            var autoClose = host.Panel.Items(b.Context.ProfileId).Single(i => i.FileName == "auto-close.bin");
            Require(b.BackgroundDownloadViewCount == 1 && autoClose.Info.Phase == DownloadPhase.InProgress, "website window.close during save choice keeps transfer");
            await b.CloseTabAsync(extra);
            Require(b.Views.Count == 1 && b.MainView!.CoreWebView2.Source == "about:blank" && !b.IsClosing, "last visible tab preserves existing background downloads");
            await Until(() => autoClose.Info.Phase == DownloadPhase.Completed && b.BackgroundDownloadViewCount == 0);
            Require(new FileInfo(autoClose.FilePath!).Length == Server.Size && File.ReadAllBytes(autoClose.FilePath!).Select((value, index) => value == (byte)(index % 251)).All(value => value), "auto-closed popup complete binary payload");
            // Explicit profile shutdown still cancels a transfer retained from a closed tab.
            host.SelectTab(a.Context, repeatedView);
            repeatedView.CoreWebView2.Navigate(server.Url + "/closing.bin");
            await Until(() => host.Panel.Items(a.Context.ProfileId).Any(i => i.FileName == "closing.bin" && i.Info.BytesReceived > 0));
            staleCancel = host.Panel.Items(a.Context.ProfileId).Single(i => i.FileName == "closing.bin").Info.Cancel;
            Require(staleCancel is not null, "old active operation command retained for close/restart check");
            await a.CloseTabAsync(repeatedView);
            Require(a.BackgroundDownloadViewCount == 1, "shutdown fixture retains background transfer");
            a.LogFile?.Dispose();
            var failedStream=new FailedFlushStream();
            var logConstructor=typeof(ConnectionLogFile).GetConstructor(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,
                [typeof(string),typeof(ConnectionLog),typeof(StreamWriter)],null) ?? throw new InvalidOperationException("Log fault fixture constructor missing.");
            a.LogFile=(ConnectionLogFile)logConstructor.Invoke(["synthetic-failure.tsv",a.Connections,new StreamWriter(failedStream)]);
            failedStream.Fail=true;host.ExpectLogFailure=true;
            await a.CloseAsync(); await a.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); host.Current.Remove(a.Context);
            Require(host.LogFailureReports==1&&failedStream.Disposed&&a.Views.Count==0&&a.BackgroundDownloadViewCount==0,"log flush failure still releases all browser/download controllers");
            host.ExpectLogFailure=false;
            Console.WriteLine("PASS: injected connection-log flush failure; expected diagnostic; stream/controller/background download resources released; actual BrowserProcessExited observed.");
            Require(host.Panel.ActiveCount(a.Context.ProfileId) == 0, "profile close clears unfinished and resumable transfers");
            var next = await Start(a.Context.ProfileId, 2);
            next.MainView!.CoreWebView2.Navigate(server.Url + "/fresh.bin");
            await Until(() => host.Panel.Items(next.Context.ProfileId).Any(i => i.Info.Context == next.Context && i.Info.BytesReceived > 0));
            var fresh = host.Panel.Items(next.Context.ProfileId).Single(i => i.Info.Context == next.Context);
            staleCancel?.Invoke(); await Task.Delay(300);
            Require(fresh.Info.Phase == DownloadPhase.InProgress && host.Panel.ActiveCount(next.Context.ProfileId) == 1, "old generation download action doesn't affect new one");
            await next.CloseAsync(); await next.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); host.Current.Remove(next.Context);
            Require(fresh.Info.Phase == DownloadPhase.Cancelled && fresh.Reason == "Профиль закрыт.", "profile close reports cancellation");
            Console.WriteLine("PASS: native download status; real parallel known/chunked transfers; bytes/rate/ETA; stable operation counts; actual WPF pause/resume/cancel; HTTP Range payload verified; truncated response human error; cancelled save dialog; closed-tab/background pause and HTTP Range resume; last-tab blank replacement; website window.close during save choice; background controller cleanup; explicit profile close; profile isolation and stale generation action rejected; screenshots captured.");
        }
        finally
        {
            foreach (var session in sessions) { await session.CloseAsync(); await session.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); host.Current.Remove(session.Context); }
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(20); while (!condition()) { if (DateTime.UtcNow > limit) throw new TimeoutException("download status fixture condition"); await Task.Delay(50); }
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var next in Children(child)) yield return next; }
    }
    private static HashSet<string> VisibleNames(DownloadsPanel panel) => Children(panel).OfType<TextBlock>().Select(t => t.Text).ToHashSet();
    private static void Click(DownloadsPanel panel, DownloadItem item, string tooltip)
    {
        panel.UpdateLayout(); Children(panel).OfType<Button>().Single(b => ReferenceEquals(b.DataContext, item) && Equals(b.ToolTip, tooltip)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Capture(DownloadsPanel panel, string name)
    {
        panel.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Render the bottom-docked control at the bitmap origin, without its parent layout offset.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(panel), null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
        bitmap.Render(visual);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Require(pixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0), "download screenshot contains rendered panel");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("artifacts/test-results"); using var output = File.Create("artifacts/test-results/" + name + ".png"); encoder.Save(output);
    }
    private static void Require(bool success, string message) { if (!success) throw new InvalidOperationException("Download status regression: " + message); }
    private sealed class FailedFlushStream : MemoryStream
    {
        public bool Fail,Disposed;
        public override void Flush() {if(Fail)throw new IOException("Synthetic log flush failure.");base.Flush();}
        protected override void Dispose(bool disposing) {Disposed=true;base.Dispose(disposing);}
    }

    private sealed class Host : IBrowserViewHost
    {
        private readonly Grid _pages = new(); private readonly string _directory;
        public DownloadsPanel Panel { get; } = new(); public HashSet<GenerationContext> Current { get; } = [];
        public HashSet<WebView2> Loaded { get; } = [];
        public bool ExpectLogFailure; public int LogFailureReports;
        public Dictionary<string, int> SaveRequests { get; } = [];
        public Dictionary<Guid, long> ProtocolCompleted { get; } = [];
        public Host(Window window, string directory) { _directory = directory; var root = new DockPanel(); DockPanel.SetDock(Panel, Dock.Bottom); root.Children.Add(Panel); root.Children.Add(_pages); window.Content = root; }
        public void Attach(GenerationContext context, WebView2 view)
        {
            _pages.Children.Add(view); view.Visibility = Visibility.Hidden; Panel.SelectProfile(context.ProfileId);
            view.CoreWebView2InitializationCompleted += (_, ready) =>
            {
                if (!ready.IsSuccess) return;
                view.CoreWebView2.GetDevToolsProtocolEventReceiver("Page.downloadWillBegin").DevToolsProtocolEventReceived += (_, e) => Console.WriteLine("Download fixture identity: " + e.ParameterObjectAsJson);
                view.CoreWebView2.GetDevToolsProtocolEventReceiver("Page.downloadProgress").DevToolsProtocolEventReceived += (_, e) =>
                {
                    if (BrowserDownloadCompletion.ReadProgress(e.ParameterObjectAsJson) is { Bytes: { } bytes } progress) ProtocolCompleted[progress.Id] = bytes;
                };
                view.CoreWebView2.DownloadStarting += (_, download) => Console.WriteLine($"Download fixture starting: bytes={download.DownloadOperation.BytesReceived}; proposed={download.ResultFilePath}; actual={download.DownloadOperation.ResultFilePath}; uri={download.DownloadOperation.Uri}");
                view.CoreWebView2.NavigationCompleted += (_, navigation) =>
                { if (navigation.IsSuccess && view.CoreWebView2.Source.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && view.CoreWebView2.Source.EndsWith('/')) Loaded.Add(view); };
            };
        }
        public void Detach(GenerationContext context, WebView2 view) => _pages.Children.Remove(view);
        public void TabReady(GenerationContext context, WebView2 view) => SelectTab(context, view);
        public void SelectTab(GenerationContext context, WebView2 view) { foreach (WebView2 browser in _pages.Children) browser.Visibility = ReferenceEquals(browser, view) ? Visibility.Visible : Visibility.Hidden; }
        public WebView2? ActiveView(GenerationContext context) => _pages.Children.OfType<WebView2>().FirstOrDefault(v => v.Visibility == Visibility.Visible);
        public Task<UserPermissionAnswer?> AskPermissionAsync(GenerationContext context, string origin, PermissionKindKey kind) => Task.FromResult<UserPermissionAnswer?>(UserPermissionAnswer.AllowOnce);
        public void OfferExternalLink(GenerationContext context, string uri) => throw new InvalidOperationException("download navigation escaped profile");
        public async Task<string?> ChooseDownloadPathAsync(GenerationContext context, string name, string? initialDirectory)
        { SaveRequests[name] = SaveRequests.GetValueOrDefault(name) + 1; if (name is "broken.bin" or "auto-close.bin") await Task.Delay(500); var directory = Path.Combine(_directory, context.ToString()); Directory.CreateDirectory(directory); return name == "save-cancel.bin" ? null : Path.Combine(directory, name == "repeat.bin" ? $"repeat-{SaveRequests[name]}.bin" : name); }
        public void ReportDownload(DownloadInfo info)
        {
            var previous = Panel.Items(info.Context.ProfileId).FirstOrDefault(i => i.Id == info.DownloadId);
            if (previous is null || previous.Info.Phase != info.Phase)
                Console.WriteLine($"Download fixture: {info.FileName} {info.DownloadId} {info.Phase} bytes={info.BytesReceived} reason={info.Reason}");
            Panel.Report(info);
        }
        public void ReportProblem(GenerationContext context, string message)
        {
            if(ExpectLogFailure&&message.StartsWith("Не удалось завершить запись журнала соединений:",StringComparison.Ordinal)) {LogFailureReports++;return;}
            throw new InvalidOperationException(message);
        }
        public Task StopProfileAsync(GenerationContext context, string message) => throw new InvalidOperationException(message);
    }
    private sealed class Server : IDisposable
    {
        public const int Size = 6 * 1024 * 1024;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = []; private readonly Task _loop; public int Ranges; public volatile bool RecoverBroken;
        public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Server() { _listener.Start(); _loop = Task.Run(Loop); }
        private async Task Loop()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(Serve(client)); } }
            catch (Exception e) when (e is OperationCanceledException || _stop.IsCancellationRequested && e is SocketException or ObjectDisposedException) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stop.Token); if (first is null) return;
                var path = first.Split(' ')[1]; long start = 0;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) { start = long.Parse(line[13..].Split('-')[0], System.Globalization.CultureInfo.InvariantCulture); Interlocked.Increment(ref Ranges); }
                if (path.EndsWith(".bin", StringComparison.Ordinal)) Console.WriteLine($"Download fixture request: {path} start={start}");
                if (!path.EndsWith(".bin", StringComparison.Ordinal))
                {
                    var body = Encoding.UTF8.GetBytes("<!doctype html><title>Downloads fixture</title>Local downloads fixture");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token); await stream.WriteAsync(body, _stop.Token); return;
                }
                var chunked = path == "/unknown.bin";
                var headers = $"HTTP/1.1 {(start > 0 ? "206 Partial Content" : "200 OK")}\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"{path[1..]}\"\r\nAccept-Ranges: bytes\r\nETag: \"download-fixture-v1\"\r\nConnection: close\r\n";
                if (start > 0) headers += $"Content-Range: bytes {start}-{Size-1}/{Size}\r\n";
                headers += chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {Size-start}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n"), _stop.Token);
                var transfer = System.Diagnostics.Stopwatch.StartNew();
                var buffer = new byte[65536];
                for (var offset = start; offset < Size; offset += buffer.Length)
                {
                    var count = (int)Math.Min(buffer.Length, Size-offset); for (var i = 0; i < count; i++) buffer[i] = (byte)((offset+i)%251);
                    if (chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("x")+"\r\n"), _stop.Token);
                    await stream.WriteAsync(buffer.AsMemory(0,count), _stop.Token);
                    if (chunked) await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                    if (path == "/broken.bin" && !RecoverBroken) return;
                    // Rate/pause fixtures are deliberately paced. Recovery tests
                    // Range/payload integrity, not a throughput SLA: avoid coupling
                    // its deadline to timers in an invisible background host.
                    if (path != "/broken.bin") await Task.Delay(150, _stop.Token);
                }
                if (path == "/broken.bin") Console.WriteLine($"Download fixture recovered Range sent: {Size-start} bytes in {transfer.Elapsed.TotalMilliseconds:F0}ms.");
                if (chunked) await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException || _stop.IsCancellationRequested && e is ObjectDisposedException) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); try { _loop.GetAwaiter().GetResult(); Task.WhenAll(_clients).GetAwaiter().GetResult(); } catch (OperationCanceledException) { } _stop.Dispose(); }
    }
}
