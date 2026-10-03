using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

internal static class UserAgentHintsBootstrap
{
    public static void Apply(CoreWebView2 core, ProfileConfig config)
    {
        // Native UA setting may clear UA Client Hints; other default modes leave it untouched.
        if (UserAgentHintsPrivacy.IsEnabled(config.GraphicsPolicy))
            core.Settings.UserAgent = UserAgentHintsPrivacy.UserAgentToApply(config, core.Settings.UserAgent);
        else if (config.UserAgentMode == UserAgentMode.Custom) core.Settings.UserAgent = config.CustomUserAgent;
    }

    public static async Task VerifyAsync(CoreWebView2 core, CoreWebView2Environment environment, ProfileConfig config, bool verify)
    {
        if (!UserAgentHintsPrivacy.IsEnabled(config.GraphicsPolicy) || !verify) return;
        // about:blank can naturally lack UAData: use a secure, host-intercepted document instead.
        // Run before navigation handlers; child controllers remain unnavigated for NewWindowRequested.
        const string uri = "https://ua-hints-bootstrap.protonprofiles.invalid/";
        var served = false;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!e.Request.Uri.StartsWith(uri, StringComparison.Ordinal)) return;
            var document = e.Request.Uri == uri && e.ResourceContext == CoreWebView2WebResourceContext.Document;
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(document
                ? "<!doctype html><meta charset=utf-8><title>Privacy bootstrap</title>"u8.ToArray() : []),
                document ? 200 : 404, document ? "OK" : "Not Found", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            served |= document;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) => completed.TrySetResult(e.IsSuccess);
        core.AddWebResourceRequestedFilter(uri + "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += Serve;
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(uri);
            if (!await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)) || !served) throw new InvalidOperationException("Локальная защищённая проверка не загрузилась.");
            var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new {
                expression = UserAgentHintsPrivacy.EvaluationScript, awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(10));
            var result = UserAgentHintsPrivacy.ReadCdpResult(json, core.Settings.UserAgent);
            if (result.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(result.Detail);
        }
        catch (Exception e) { throw new InvalidOperationException("Ограничение UA Client Hints не подтверждено; открытие заблокировано. " + e.Message, e); }
        finally
        {
            core.NavigationCompleted -= Completed;
            // Keep the origin-local response filter for this controller's lifetime: late favicon/subresource
            // requests must also stay local after navigation completion. Controller disposal releases handlers.
        }
    }

}
