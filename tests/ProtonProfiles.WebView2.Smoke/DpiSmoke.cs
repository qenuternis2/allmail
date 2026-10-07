using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.App;
using ProtonProfiles.App.Browser;
using ProtonProfiles.App.Dialogs;
using ProtonProfiles.App.ViewModels;
using ProtonProfiles.Core;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Permissions;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

/// <summary>Actual production HWNDs. A synthetic notification is never reported as an OS scale change.</summary>
internal static class DpiSmoke
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, ref NativeRect lParam);
    private static readonly List<object> Results = [];

    public static async Task RunAsync(string root, string runtimeVersion)
    {
        Results.Clear();
        using var resolution = DisplayResolutionFixture.TryResize();
        if (resolution is not null) await Task.Delay(500);
        var paths = new ManagedPaths(Path.Combine(root, "dpi-ui")); paths.EnsureBaseDirectories();
        var repository = new SqliteProfileRepository(paths.DatabasePath, paths.BackupsRoot);
        var credentials = new InMemoryCredentialStore();
        var catalog = new ProfileCatalog(repository, credentials);
        var permissions = new PermissionPolicy(repository);
        using var updater = new MailfudGeoIpUpdater(paths);
        using var site = new GeoIpTimeZoneSmoke.IpServer(false, html: true);
        catalog.Create("DPI fixture · Проверка", null, "#0060DF", out var profile);
        profile = ProfileStartPage.WithUrl(profile!, $"http://127.0.0.1:{site.Port}/index");
        repository.Update(profile);
        var shell = new MainWindow(paths, repository, catalog, credentials, permissions, runtimeVersion, updater)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30, Width = 900, Height = 560 };
        var engine = new WebView2Engine(shell, paths, permissions, new NavigationPolicy(), credentials);
        shell.Initialize(engine);
        var lifecycle = (ProfileLifecycleService)typeof(MainWindow).GetField("_lifecycle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(shell)!;
        using var desktop = DisplayDpiFixture.TryCapture();
        try
        {
            shell.Show(); await Layout(shell);
            var hwnd = new WindowInteropHelper(shell).Handle;
            Require(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd), new IntPtr(-4)), "production PerMonitorV2 manifest");
            Require((await lifecycle.OpenAsync(profile.Id)).Outcome == OpenOutcome.Opened, "local profile opened");
            typeof(MainWindow).GetMethod("Reload", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(shell, null);
            var list = (ListBox)shell.FindName("ProfileList");
            list.SelectedItem = list.Items.OfType<ProfileItem>().Single(p => p.Id == profile.Id);
            var view = ((WebView2Session)lifecycle.GetSession(profile.Id)!).Views[0];
            for (var i = 0; await view.CoreWebView2.ExecuteScriptAsync("document.title==='A18 fixture'&&document.readyState==='complete'") != "true"; i++)
            { if (i >= 100) throw new TimeoutException("DPI local page did not load."); await Task.Delay(50); }
            await view.CoreWebView2.ExecuteScriptAsync("localStorage.dpi='preserved';document.body.innerHTML='<button id=action onclick=\"this.dataset.clicked=1\">DPI local control</button>'");
            foreach (var percent in new[] { 100, 125, 150, 200 })
            {
                var target = 96 * percent / 100;
                var real = desktop?.TrySet(percent) == true;
                if (real)
                {
                    for (var i = 0; i < 50 && GetDpiForWindow(hwnd) != target; i++) await Task.Delay(100);
                    Require(GetDpiForWindow(hwnd) == target, "OS scale change acknowledged by HWND at " + percent);
                }
                var mode = real ? "DesktopScale" : "SyntheticWM_DPICHANGED";
                if (!real) Console.WriteLine($"DPI {percent}% desktop change NotPerformed; testing native WM_DPICHANGED separately.");
                await Scale(shell, target, real);
                Check(shell, "main", percent, mode);
                ((Button)shell.FindName("CollapseProfilesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Layout(shell);
                Check(shell, "main-sidebar-collapsed", percent, mode);
                Require(Math.Abs(((ColumnDefinition)shell.FindName("ProfilesColumn")).ActualWidth - 40) < 1
                    && ((Button)shell.FindName("ExpandProfilesButton")).IsVisible, "restore strip available at " + percent);
                ((Button)shell.FindName("ExpandProfilesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Layout(shell);
                Require(((FrameworkElement)shell.FindName("ProfilesPanel")).IsVisible, "sidebar restored at " + percent);
                var browser = await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({dpr:devicePixelRatio,width:innerWidth,height:innerHeight,storage:localStorage.dpi,title:document.title})");
                using var observation = JsonDocument.Parse(JsonSerializer.Deserialize<string>(browser)!);
                var page = observation.RootElement;
                Require(page.GetProperty("storage").GetString() == "preserved" && page.GetProperty("title").GetString() == "A18 fixture"
                    && page.GetProperty("width").GetInt32() > 100 && page.GetProperty("height").GetInt32() > 100, "live viewport and session retained");
                if (real) Require(Math.Abs(page.GetProperty("dpr").GetDouble() - target / 96d) < .01, "browser follows real desktop DPI");
                Require(await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('action').click();document.getElementById('action').dataset.clicked==='1'") == "true", "page remains interactive");
                using (var preview = File.Create($"artifacts/test-results/dpi-{percent}-browser.png"))
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
                Results.Add(new { Window = "browser", RequestedPercent = percent, Mode = mode, NativeDpi = GetDpiForWindow(hwnd),
                    BrowserDpr = page.GetProperty("dpr").GetDouble(), CssWidth = page.GetProperty("width").GetInt32(), CssHeight = page.GetProperty("height").GetInt32(), Status = "Pass" });
                var created = new NewProfileWindow(shell, "#0060DF", repository.ListGroups(), null);
                await Modal(created, target, real, () => Check(created, "create", percent, mode));
                var editor = new ProfileEditorWindow(shell, profile!, engine.Capabilities, paths, updater);
                await Modal(editor, target, real, () =>
                {
                    var tabs = Visuals(editor).OfType<TabControl>().Single();
                    for (var i = 0; i < tabs.Items.Count; i++)
                    { tabs.SelectedIndex = i; editor.UpdateLayout(); Check(editor, "settings-" + i, percent, mode); }
                });
                var groups = new ProfileGroupsWindow(shell, repository);
                groups.Show();
                try { await Scale(groups, target, real); Check(groups, "groups", percent, mode); }
                finally { groups.Close(); }
                Exception? failure = null;
                _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "DPI confirmation");
                    try { await Scale(dialog, target, real); Check(dialog, "confirmation", percent, mode); }
                    catch (Exception e) { failure = e; }
                    finally { dialog.Close(); }
                }), DispatcherPriority.ApplicationIdle);
                ChoiceDialog.Show(shell, "DPI confirmation", "Проверка читаемого текста, кнопок и масштабирования окна.", ["Продолжить", "Отмена"], 0, 1);
                if (failure is not null) throw failure;
            }
            Require(repository.ListProfiles().Count == 1, "DPI checks preserve metadata");
            Console.WriteLine("PASS: DPI production shell including collapsed/restored profiles strip, creation, all five settings tabs, groups and confirmation; 100/125/150/200%; manifest, WPF scale, visible controls and PNG captures. See dpi-results.json for actual OS vs synthetic coverage.");
        }
        finally
        {
            try
            {
                if (lifecycle.GetSession(profile.Id) is not null)
                    Require((await lifecycle.CloseAsync(profile.Id)).Outcome == CloseOutcome.Closed, "DPI browser fully exited");
            }
            finally
            {
                shell.Close();
                Directory.CreateDirectory("artifacts/test-results");
                File.WriteAllText("artifacts/test-results/dpi-results.json", JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private static async Task Modal(Window window, int target, bool real, Action check)
    {
        Exception? failure = null;
        _ = window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Scale(window, target, real); check(); }
            catch (Exception e) { failure = e; }
            finally { window.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        window.ShowDialog();
        if (failure is not null) throw failure;
    }

    private static async Task Scale(Window window, int target, bool real)
    {
        await Layout(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!real)
        {
            Require(GetWindowRect(hwnd, out var rect), "native window bounds");
            var scale = target / (96 * VisualTreeHelper.GetDpi(window).DpiScaleX);
            rect.Right = rect.Left + (int)Math.Round((rect.Right - rect.Left) * scale);
            rect.Bottom = rect.Top + (int)Math.Round((rect.Bottom - rect.Top) * scale);
            SendMessage(hwnd, 0x02E0, new IntPtr(target | target << 16), ref rect);
        }
        await Layout(window);
        Require(Math.Abs(VisualTreeHelper.GetDpi(window).PixelsPerInchX - target) < .1, "WPF DPI notification " + window.Title);
        if (real) Require(GetDpiForWindow(hwnd) == target, "native OS DPI " + window.Title);
    }

    private static void Check(Window window, string name, int percent, string mode)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var rect);
        var content = (FrameworkElement)window.Content;
        var buttons = Visuals(window).OfType<Button>().Where(b => b.IsVisible).ToArray();
        foreach (var button in buttons)
        {
            // IsVisible includes off-viewport ScrollViewer content. Verify that
            // each action can be revealed, rather than flagging ordinary scrolling.
            button.BringIntoView(); window.UpdateLayout();
            var point = button.TranslatePoint(new Point(), content);
            Require(button.ActualWidth > 0 && button.ActualHeight > 0 && point.X >= 0 && point.Y >= 0
                && point.X + button.ActualWidth <= content.ActualWidth + 1 && point.Y + button.ActualHeight <= content.ActualHeight + 1,
                $"button inside {name} at {percent}%: {button.Content}");
        }
        // Rendering the margin-bearing content directly also renders its parent
        // offset, clipping the footer. Render the Window into its actual client
        // pixel bounds; the non-client title bar is deliberately excluded.
        Require(GetClientRect(hwnd, out var client), "native client pixel bounds");
        var bitmap = new RenderTargetBitmap(client.Right, client.Bottom, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, client.Right / dpi.DpiScaleX, client.Bottom / dpi.DpiScaleY));
        bitmap.Render(background); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/test-results");
        using (var output = File.Create($"artifacts/test-results/dpi-{percent}-{name}.png")) encoder.Save(output);
        Results.Add(new { Window = name, RequestedPercent = percent, Mode = mode, NativeDpi = GetDpiForWindow(hwnd),
            WpfDpi = dpi.PixelsPerInchX, PhysicalWidth = rect.Right - rect.Left, PhysicalHeight = rect.Bottom - rect.Top,
            BitmapWidth = bitmap.PixelWidth, BitmapHeight = bitmap.PixelHeight, VisibleButtons = buttons.Length, Status = "Pass" });
        Console.WriteLine($"DPI {percent}% {name}: {mode}; HWND={GetDpiForWindow(hwnd)}, WPF={dpi.PixelsPerInchX}, PNG={bitmap.PixelWidth}x{bitmap.PixelHeight}, buttons={buttons.Length}.");
    }

    private static async Task Layout(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Visuals(child)) yield return descendant;
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("DPI regression: " + message); }
}
