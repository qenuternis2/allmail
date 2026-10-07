using Microsoft.Web.WebView2.Core;

namespace ProtonProfiles.App.Browser;

/// <summary>Checks the configured website through this controller before a proxy revision is applied.</summary>
internal static class ProxyStartupCheck
{
    public static async Task NavigateAsync(CoreWebView2 core, string address, CancellationToken cancellationToken,
        bool restoreServiceWorkers)
    {
        var completed = new TaskCompletionSource<(int Status, CoreWebView2WebErrorStatus Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        var confirmed = false;
        var observed = new HashSet<string>(StringComparer.Ordinal);
        static string Key(string uri)
        {
            var parsed = new Uri(uri);
            return $"{parsed.Scheme}://{parsed.IdnHost.ToLowerInvariant()}:{parsed.Port}{parsed.PathAndQuery}";
        }
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (navigationId is null && !e.IsRedirected && Key(e.Uri) == Key(address)) navigationId = e.NavigationId;
            if (navigationId == e.NavigationId) observed.Add(Key(e.Uri));
        }
        void Response(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            // Receiving headers is sufficient for connectivity. Waiting for document
            // completion rejects valid sites that redirect with JavaScript or stop loading.
            var status = e.Response.StatusCode;
            if (status is >= 200 and <= 599 and not 407 && observed.Contains(Key(e.Request.Uri)))
                completed.TrySetResult((status, CoreWebView2WebErrorStatus.Unknown));
        }
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId == e.NavigationId) completed.TrySetResult((e.HttpStatusCode, e.WebErrorStatus));
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Finished;
        core.WebResourceResponseReceived += Response;
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
            core.WebResourceResponseReceived -= Response;
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
