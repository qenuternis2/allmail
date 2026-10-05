using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

internal static class BrowserPermissionRequests
{
    private sealed record Guard(Func<bool> IsCurrent);
    private static readonly ConditionalWeakTable<CoreWebView2, Guard> Guards = new();
    public static bool IsReady(CoreWebView2 core) => Guards.TryGetValue(core, out var guard) && guard.IsCurrent();

    private static bool Blocked(ProfileConfig config, CoreWebView2PermissionKind kind) =>
        AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy) && kind switch
        {
            CoreWebView2PermissionKind.Notifications or CoreWebView2PermissionKind.ClipboardRead => false,
            CoreWebView2PermissionKind.Camera => !ProfilePrivacy.Allows(config, PrivacyException.Camera),
            CoreWebView2PermissionKind.Microphone => !ProfilePrivacy.Allows(config, PrivacyException.Microphone),
            _ => true
        };

    public static async Task InstallAsync(CoreWebView2 core, ProfileConfig config, Func<bool> isCurrent,
        Action<CoreWebView2PermissionRequestedEventArgs>? allowedRequest = null, bool denyAll = false,
        Action<string>? diagnostic = null)
    {
        void Requested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            if (!isCurrent() || denyAll || Blocked(config, e.PermissionKind) || allowedRequest is null)
            {
                e.SavesInProfile = false;
                e.State = CoreWebView2PermissionState.Deny;
                e.Handled = true;
                diagnostic?.Invoke("Native permission request denied: " + e.PermissionKind);
            }
            else allowedRequest(e);
        }
        // Install for documents and cross-origin frames before any secure bootstrap or website.
        core.PermissionRequested += Requested;
        core.FrameCreated += (_, frame) => frame.Frame.PermissionRequested += Requested;
        if (!AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy)) return;
        // Stored Allow decisions bypass PermissionRequested. Erase incompatible grants, while
        // leaving exception-enabled camera/microphone and the authoritative app policy intact.
        var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var setting in settings)
            if (setting.PermissionState == CoreWebView2PermissionState.Allow && Blocked(config, setting.PermissionKind))
            {
                await core.Profile.SetPermissionStateAsync(setting.PermissionKind, setting.PermissionOrigin, CoreWebView2PermissionState.Default)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                diagnostic?.Invoke("Stored native permission grant erased: " + setting.PermissionKind);
            }
        if (!isCurrent()) throw new InvalidOperationException("Профиль закрывается.");
        Guards.Add(core, new(isCurrent));
        diagnostic?.Invoke("Native permission request guard ready; conflicting stored grants erased.");
    }
}
