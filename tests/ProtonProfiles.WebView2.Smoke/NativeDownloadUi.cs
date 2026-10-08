using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;

// Diagnostic-only: inspect this disposable fixture's native HWNDs, never other applications.
internal static class NativeDownloadUi
{
    internal sealed record HubObservation(JsonElement Dom, Observation[] Windows);
    internal static async Task<HubObservation> InspectHubAsync(WebView2Session session)
    {
        // A separate diagnostic controller avoids changing the production guard's CDP session.
        using var inspector = new WebView2();
        var host = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Content = inspector };
        host.Show();
        try
        {
            await inspector.EnsureCoreWebView2Async(session.Environment, session.ControllerOptions);
            var core = inspector.CoreWebView2;
            using var targets = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
            var target = targets.RootElement.GetProperty("targetInfos").EnumerateArray()
                .Single(t => t.GetProperty("url").GetString() == "edge://downloads-hub/").GetProperty("targetId").GetString();
            using var attached = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.attachToTarget",
                JsonSerializer.Serialize(new { targetId = target, flatten = true })));
            var id = attached.RootElement.GetProperty("sessionId").GetString()!;
            const string expression = "(()=>{const nodes=[];function walk(root){for(const e of root.querySelectorAll('*')){if(e.shadowRoot)walk(e.shadowRoot);if(e.tagName!=='F-TEMPLATE'&&(e.tagName.includes('-')||e.matches('button,a,[role=button]')))nodes.push({tag:e.tagName,action:e.getAttribute('data-action'),label:e.getAttribute('aria-label')||e.title,text:e.innerText,disabled:e.hasAttribute('disabled'),visible:e.checkVisibility({checkVisibilityCSS:true,checkOpacity:true})});}}walk(document);return nodes;})()";
            // Locate exactly this fixture's More actions button. Send a real browser
            // mouse gesture to reveal its menu, never Keep/Open/Run or the file itself.
            const string locateMenu = "(()=>{function find(root){for(const e of root.querySelectorAll('*')){if(e.tagName==='DOWNLOAD-ITEM'&&e.title.startsWith(\"FindCopy-win-x64.exe isn't commonly downloaded\")){const b=e.shadowRoot.querySelector('[data-action=moreActions]');if(b){const r=b.getBoundingClientRect();return {found:r.width>0&&r.height>0,x:r.x+r.width/2,y:r.y+r.height/2};}}if(e.shadowRoot){const p=find(e.shadowRoot);if(p.found)return p;}}return {found:false};}return find(document);})()";
            using var menu = JsonDocument.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(id, "Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = locateMenu, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(10)));
            var point = menu.RootElement.GetProperty("result").GetProperty("value");
            var found = point.GetProperty("found").GetBoolean();
            if (found)
            {
                var x = point.GetProperty("x").GetDouble(); var y = point.GetProperty("y").GetDouble();
                await core.CallDevToolsProtocolMethodForSessionAsync(id, "Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
                await Task.Delay(500);
                await core.CallDevToolsProtocolMethodForSessionAsync(id, "Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", button = "left", clickCount = 1, x, y }));
                await core.CallDevToolsProtocolMethodForSessionAsync(id, "Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", button = "left", clickCount = 1, x, y }));
                await Task.Delay(500);
            }
            using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(id, "Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(10)));
            var menuWindows = await ObserveAsync(Window.GetWindow(session.MainView!)!, session.Environment, "open-warning-menu");
            using var screenshot = JsonDocument.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(id, "Page.captureScreenshot", "{}"));
            File.WriteAllBytes("artifacts/test-results/download-menu-" + (found ? "visible" : "hidden") + ".png",
                Convert.FromBase64String(screenshot.RootElement.GetProperty("data").GetString()!));

            await core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", JsonSerializer.Serialize(new { sessionId = id }));
            return new(result.RootElement.Clone(), menuWindows);
        }
        finally { host.Close(); }
    }
    internal sealed record Observation(string Class, string Title, bool Visible, int Width, int Height, string[] Names);
    internal static async Task<Observation[]> ObserveAsync(Window window, CoreWebView2Environment environment, string label)
    {
        var pids = environment.GetProcessInfos().Select(p => p.ProcessId).Append(System.Environment.ProcessId).ToHashSet();
        var handles = new HashSet<IntPtr> { new WindowInteropHelper(window).Handle };
        bool Collect(IntPtr hwnd, IntPtr parameter)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains((int)pid)) handles.Add(hwnd);
            return true;
        }
        EnumWindows(Collect, IntPtr.Zero);
        foreach (var hwnd in handles.ToArray()) EnumChildWindows(hwnd, Collect, IntPtr.Zero);
        var results = new List<Observation>();
        foreach (var hwnd in handles)
        {
            var klass = new StringBuilder(256); GetClassName(hwnd, klass, klass.Capacity);
            if (!klass.ToString().StartsWith("Chrome", StringComparison.Ordinal) && klass.ToString() != "#32768") continue;
            var title = new StringBuilder(256); GetWindowText(hwnd, title, title.Capacity);
            GetWindowRect(hwnd, out var rect);
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            string[] names;
            try
            {
                names = await Task.Run(() => AutomationElement.FromHandle(hwnd)
                    .FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                    .Select(e => e.Current.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(500).ToArray())
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e) when (e is TimeoutException or ElementNotAvailableException or COMException) { names = [e.GetType().Name]; }
            if (!names.Contains("Downloads") && !names.Contains("Keep")) continue;
            if (IsWindowVisible(hwnd) && width >= 100 && height >= 100)
                Capture(hwnd, width, height, "artifacts/test-results/native-" + label + "-" + results.Count + ".png");
            results.Add(new(klass.ToString(), title.ToString(), IsWindowVisible(hwnd), width, height, names));
        }
        return results.ToArray();
    }
    private static void Capture(IntPtr hwnd, int width, int height, string path)
    {
        var dc = GetDC(hwnd); var target = CreateCompatibleDC(dc);
        var bitmap = CreateCompatibleBitmap(dc, width, height); var previous = SelectObject(target, bitmap);
        try
        {
            if (!PrintWindow(hwnd, target, 2)) return;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); png.Save(output);
        }
        finally { SelectObject(target, previous); DeleteObject(bitmap); DeleteDC(target); ReleaseDC(hwnd, dc); }
    }
    private delegate bool EnumerateWindow(IntPtr hwnd, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumerateWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
