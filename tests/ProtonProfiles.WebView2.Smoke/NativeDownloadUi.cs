using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

// Diagnostic-only: inspect this disposable fixture's native HWNDs, never other applications.
internal static class NativeDownloadUi
{
    internal static async Task HoverWarningAsync(CoreWebView2Environment environment)
    {
        var pids = environment.GetProcessInfos().Select(p => p.ProcessId).ToHashSet();
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) => { GetWindowThreadProcessId(hwnd, out var pid); if (pids.Contains((int)pid)) windows.Add(hwnd); return true; }, IntPtr.Zero);
        foreach (var parent in windows.ToArray()) EnumChildWindows(parent, (hwnd, _) => { windows.Add(hwnd); return true; }, IntPtr.Zero);
        foreach (var hwnd in windows.Distinct())
        {
            var klass = new StringBuilder(256); GetClassName(hwnd, klass, klass.Capacity);
            if (klass.ToString() != "Chrome_RenderWidgetHostHWND") continue;
            var root = AutomationElement.FromHandle(hwnd);
            var elements = root.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().ToArray();
            if (!elements.Any(e => e.Current.Name == "Downloads")
                || !elements.Any(e => e.Current.Name.StartsWith("FindCopy-win-x64.exe isn't commonly downloaded", StringComparison.Ordinal))) continue;
            GetWindowRect(hwnd, out var rect);
            // Only hover this fixture's warning row. No file safety decision or file is opened.
            PostMessage(hwnd, 0x0200, IntPtr.Zero, new IntPtr(((75 & 0xffff) << 16) | ((rect.Right - rect.Left - 20) & 0xffff)));
            await Task.Delay(500);
        }
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
            if (!klass.ToString().StartsWith("Chrome", StringComparison.Ordinal)) continue;
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
            if (!names.Contains("Downloads")) continue;
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
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
