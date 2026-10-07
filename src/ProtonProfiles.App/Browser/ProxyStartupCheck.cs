using Microsoft.Web.WebView2.Core;

namespace ProtonProfiles.App.Browser;

/// <summary>Checks the configured website through this controller before a proxy revision is applied.</summary>
internal static class ProxyStartupCheck
{
    public static async Task NavigateAsync(CoreWebView2 core, string address, CancellationToken cancellationToken,
        bool restoreServiceWorkers)
    {
        var completed = new TaskCompletionSource<(bool Success, int Status, CoreWebView2WebErrorStatus Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        var confirmed = false;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!e.IsRedirected && e.Uri == address) navigationId = e.NavigationId;
        }
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId == e.NavigationId) completed.TrySetResult((e.IsSuccess, e.HttpStatusCode, e.WebErrorStatus));
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Finished;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
            await core.CallDevToolsProtocolMethodAsync("Network.setBypassServiceWorker", "{\"bypass\":true}");
            cancellationToken.ThrowIfCancellationRequested();
            core.Navigate(address);
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            // A site's 401/403/429 challenge still proves that an HTTP response arrived.
            // Proxy authentication and transport/TLS failures must keep the revision pending.
            var receivedResponse = result.Status is >= 200 and <= 599 and not 407
                && result.Error is CoreWebView2WebErrorStatus.Unknown
                    or CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse
                    or CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired;
            if (!receivedResponse)
                throw new InvalidOperationException($"Проверка подключения через прокси не прошла: {result.Error}, HTTP {result.Status}. Настройки не применены.");
            confirmed = true;
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Finished;
            // The controller can be disposed by a concurrent close. Preserve the original
            // startup/cancellation error instead of replacing it with a cleanup error.
            try
            {
                if (!completed.Task.IsCompleted) core.Stop();
                await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":false}");
                if (restoreServiceWorkers)
                    await core.CallDevToolsProtocolMethodAsync("Network.setBypassServiceWorker", "{\"bypass\":false}");
            }
            catch (Exception) when (!confirmed || cancellationToken.IsCancellationRequested) { }
        }
    }
}
