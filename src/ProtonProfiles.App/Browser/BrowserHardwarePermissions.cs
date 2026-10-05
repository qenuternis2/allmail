using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

internal static class BrowserHardwarePermissions
{
    public static async Task ApplyAsync(CoreWebView2 core, ProfileConfig config, Action<string>? diagnostic = null)
    {
        if (!AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy)) return;
        // A named WebView2 profile can differ from the default BrowserContext.
        using var target = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}").WaitAsync(TimeSpan.FromSeconds(10)));
        var info = target.RootElement.GetProperty("targetInfo");
        var browserContextId = info.TryGetProperty("browserContextId", out var id) ? id.GetString() : null;
        if (browserContextId == string.Empty) browserContextId = null;
        using var contexts = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getBrowserContexts", "{}").WaitAsync(TimeSpan.FromSeconds(10)));
        diagnostic?.Invoke("Browser permission contexts: " + contexts.RootElement.GetRawText() + "; profile=" + core.Profile.ProfileName);
        var root = contexts.RootElement;
        var defaultId = root.TryGetProperty("defaultBrowserContextId", out var defaultContext) ? defaultContext.GetString() : null;
        if (browserContextId == defaultId || defaultId is null && !root.GetProperty("browserContextIds").EnumerateArray().Any(value => value.GetString() == browserContextId))
            browserContextId = null;
        diagnostic?.Invoke("Browser permission scope: current controller; explicit context=" + (browserContextId is not null));
        foreach (var permission in AdditionalFingerprintPrivacy.PermissionsToDeny(config.PrivacyExceptions))
            await core.CallDevToolsProtocolMethodAsync("Browser.setPermission", AdditionalFingerprintPrivacy.PermissionArguments(permission, browserContextId)).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
