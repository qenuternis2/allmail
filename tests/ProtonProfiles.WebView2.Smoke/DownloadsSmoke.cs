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
            sessions.Add(session); await Until(() => host.Loaded.Contains(session.MainView!)); return session;
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
            Click(host.Panel, known, "Продолжить загрузку");
            await Until(() => known.Info.Phase == DownloadPhase.Completed);
            Require(File.Exists(known.FilePath) && new FileInfo(known.FilePath!).Length == Server.Size && known.Percent == 100 && host.Panel.ActiveCount(a.Context.ProfileId) == 0, "completed file and accurate final state");
            var data = File.ReadAllBytes(known.FilePath!);
            Require(data.Select((value, index) => value == (byte)(index % 251)).All(value => value), "resume retained complete binary payload");
            a.MainView!.CoreWebView2.Navigate(server.Url + "/broken.bin");
            await Until(() => host.Panel.Items(a.Context.ProfileId).Any(i => i.FileName == "broken.bin" && i.Info.Phase == DownloadPhase.Interrupted && i.Info.Resume is not null));
            var brokenItems = host.Panel.Items(a.Context.ProfileId).Where(i => i.FileName == "broken.bin").ToArray();
            Require(brokenItems.Length == 1, "one truncated download operation: " + string.Join("; ", brokenItems.Select(i => $"{i.Id} {i.Info.Phase} {i.Info.BytesReceived} {i.Reason}")));
            var broken = brokenItems.Single();
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
            var bDownload = host.Panel.Items(b.Context.ProfileId).Single(i => i.FileName == "other.bin");
            await b.CloseTabAsync(bOrigin);
            await Until(() => bDownload.Info.Phase == DownloadPhase.Cancelled);
            Require(bDownload.Reason == "Вкладка закрыта." && host.Panel.ActiveCount(b.Context.ProfileId) == 0, "closing an originating tab stops its operation");
            origin.CoreWebView2.Navigate(server.Url + "/closing.bin");
            await Until(() => host.Panel.Items(a.Context.ProfileId).Any(i => i.FileName == "closing.bin" && i.Info.BytesReceived > 0));
            staleCancel = host.Panel.Items(a.Context.ProfileId).Single(i => i.FileName == "closing.bin").Info.Cancel;
            Require(staleCancel is not null, "old active operation command retained for close/restart check");
            await a.CloseAsync(); await a.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); host.Current.Remove(a.Context);
            Require(host.Panel.ActiveCount(a.Context.ProfileId) == 0, "profile close clears unfinished and resumable transfers");
            var next = await Start(a.Context.ProfileId, 2);
            next.MainView!.CoreWebView2.Navigate(server.Url + "/fresh.bin");
            await Until(() => host.Panel.Items(next.Context.ProfileId).Any(i => i.Info.Context == next.Context && i.Info.BytesReceived > 0));
            var fresh = host.Panel.Items(next.Context.ProfileId).Single(i => i.Info.Context == next.Context);
            staleCancel?.Invoke(); await Task.Delay(300);
            Require(fresh.Info.Phase == DownloadPhase.InProgress && host.Panel.ActiveCount(next.Context.ProfileId) == 1, "old generation download action doesn't affect new one");
            await next.CloseAsync(); await next.ProcessExited.WaitAsync(TimeSpan.FromSeconds(15)); host.Current.Remove(next.Context);
            Require(fresh.Info.Phase == DownloadPhase.Cancelled && fresh.Reason == "Профиль закрыт.", "profile close reports cancellation");
            Console.WriteLine("PASS: native download status; real parallel known/chunked transfers; bytes/rate/ETA; stable operation counts; actual WPF pause/resume/cancel; HTTP Range payload verified; truncated response human error; cancelled save dialog; originating-tab/profile close; profile isolation and stale generation action rejected; screenshots captured.");
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
    private sealed class Host : IBrowserViewHost
    {
        private readonly Grid _pages = new(); private readonly string _directory;
        public DownloadsPanel Panel { get; } = new(); public HashSet<GenerationContext> Current { get; } = [];
        public HashSet<WebView2> Loaded { get; } = [];
        public Dictionary<string, int> SaveRequests { get; } = [];
        public Host(Window window, string directory) { _directory = directory; var root = new DockPanel(); DockPanel.SetDock(Panel, Dock.Bottom); root.Children.Add(Panel); root.Children.Add(_pages); window.Content = root; }
        public void Attach(GenerationContext context, WebView2 view)
        {
            _pages.Children.Add(view); view.Visibility = Visibility.Hidden; Panel.SelectProfile(context.ProfileId);
            view.CoreWebView2InitializationCompleted += (_, ready) =>
            {
                if (!ready.IsSuccess) return;
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
        public Task<string?> ChooseDownloadPathAsync(GenerationContext context, string name, string? initialDirectory)
        { SaveRequests[name] = SaveRequests.GetValueOrDefault(name) + 1; var directory = Path.Combine(_directory, context.ToString()); Directory.CreateDirectory(directory); return Task.FromResult(name == "save-cancel.bin" ? null : Path.Combine(directory, name)); }
        public void ReportDownload(DownloadInfo info)
        {
            var previous = Panel.Items(info.Context.ProfileId).FirstOrDefault(i => i.Id == info.DownloadId);
            if (previous is null || previous.Info.Phase != info.Phase)
                Console.WriteLine($"Download fixture: {info.FileName} {info.DownloadId} {info.Phase} bytes={info.BytesReceived} reason={info.Reason}");
            Panel.Report(info);
        }
        public void ReportProblem(GenerationContext context, string message) => throw new InvalidOperationException(message);
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
                var buffer = new byte[65536];
                for (var offset = start; offset < Size; offset += buffer.Length)
                {
                    var count = (int)Math.Min(buffer.Length, Size-offset); for (var i = 0; i < count; i++) buffer[i] = (byte)((offset+i)%251);
                    if (chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("x")+"\r\n"), _stop.Token);
                    await stream.WriteAsync(buffer.AsMemory(0,count), _stop.Token);
                    if (chunked) await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                    if (path == "/broken.bin" && !RecoverBroken) return;
                    // Keep real transfers active long enough for progress sampling on a busy Windows runner.
                    await Task.Delay(150, _stop.Token);
                }
                if (chunked) await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException || _stop.IsCancellationRequested && e is ObjectDisposedException) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); try { _loop.GetAwaiter().GetResult(); Task.WhenAll(_clients).GetAwaiter().GetResult(); } catch (OperationCanceledException) { } _stop.Dispose(); }
    }
}
