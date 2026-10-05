using System.Reflection;
using Microsoft.Web.WebView2.Wpf;

namespace ProtonProfiles.App.Browser;

/// <summary>Adapt the pinned WPF SDK's automatic parent-window close to a tab host.
/// The SDK exposes no switch for this behavior. Keep its other lifecycle/keyboard handlers intact.</summary>
internal static class BrowserWindowCloseHandling
{
    public static void UseTabOwnership(WebView2 view)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(WebView2).GetField("m_webview2Base", flags);
        var target = field?.GetValue(view);
        var method = target?.GetType().GetMethod("CoreWebView2_WindowCloseRequested", flags);
        if (target is null || method is null || method.DeclaringType?.Assembly != typeof(WebView2).Assembly)
            throw new InvalidOperationException("SDK WebView2 не поддерживает настройку закрытия вкладок; открытие заблокировано.");
        // Delegate equality uses the original target and method, so only this SDK subscriber is removed.
        // A changed SDK layout/signature fails before any website navigation; native CI tests this adapter.
        var handler = method.CreateDelegate<EventHandler<object>>(target);
        view.CoreWebView2.WindowCloseRequested -= handler;
    }
}
