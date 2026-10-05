using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

internal static class BrowserHardwarePermissions
{
    public static async Task ApplyAsync(CoreWebView2 core, ProfileConfig config, Action<string>? diagnostic = null)
    {
        if (!AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy)) return;
        // Controllers use the runtime's persistent default profile. WebView2 does not expose
        // Target.getBrowserContexts or accept named contexts in Browser.setPermission.
        diagnostic?.Invoke("Browser permission scope: runtime default; profile=" + core.Profile.ProfileName);
        foreach (var permission in AdditionalFingerprintPrivacy.PermissionsToDeny(config.PrivacyExceptions))
            await core.CallDevToolsProtocolMethodAsync("Browser.setPermission", AdditionalFingerprintPrivacy.PermissionArguments(permission)).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
