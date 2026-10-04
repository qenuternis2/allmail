using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

internal static class UserAgentHintsBootstrap
{
    private sealed record CpuSetting(int Count);
    private static readonly ConditionalWeakTable<CoreWebView2,CpuSetting> CpuSettings = new();
    public static int? ExpectedCpu(CoreWebView2 core) => CpuSettings.TryGetValue(core,out var setting) ? setting.Count : null;
    public static async Task ApplyAsync(CoreWebView2 core, ProfileConfig config, Func<bool>? current = null, Func<string,Task>? onFailure = null, Action<string>? diagnostic = null)
    {
        var restrictUserAgent = UserAgentHintsPrivacy.IsEnabled(config.GraphicsPolicy);
        if (!restrictUserAgent)
        {
            if (config.UserAgentMode == UserAgentMode.Custom) core.Settings.UserAgent = config.CustomUserAgent;
            if (config.BrowserTimeZoneId is null) return;
        }
        var userAgent = restrictUserAgent ? UserAgentHintsPrivacy.UserAgentToApply(config,core.Settings.UserAgent) : core.Settings.UserAgent ?? string.Empty;
        int? cpu = null;
        if (HardwareConcurrencyPrivacy.IsEnabled(config.GraphicsPolicy))
        {
            var native = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"navigator.hardwareConcurrency\",\"returnByValue\":true}").WaitAsync(TimeSpan.FromSeconds(10));
            cpu = HardwareConcurrencyPrivacy.Normalize(HardwareConcurrencyPrivacy.ReadNativeCdpCount(native));
            CpuSettings.Remove(core);
            CpuSettings.Add(core,new(cpu.Value));
        }
        if (AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy))
            foreach(var permission in AdditionalFingerprintPrivacy.PermissionsToDeny(config.PrivacyExceptions))
                await core.CallDevToolsProtocolMethodAsync("Browser.setPermission",AdditionalFingerprintPrivacy.PermissionArguments(permission)).WaitAsync(TimeSpan.FromSeconds(10));
        var protocol = new UserAgentHintsProtocol(core,userAgent,current ?? (()=>true),onFailure,diagnostic,cpu,StandardFingerprintPrivacy.IsEnabled(config.GraphicsPolicy),config.BrowserTimeZoneId,restrictUserAgent,config.PrivacyExceptions);
        await protocol.InitializeAsync();
    }

    public static async Task VerifyAsync(CoreWebView2 core, CoreWebView2Environment environment, ProfileConfig config, bool verify, Action<string>? diagnostic = null)
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
                ? "<!doctype html><meta charset=utf-8><meta name=text-scale content=scale><title>Privacy bootstrap</title>"u8.ToArray() : []),
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
            var result = UserAgentHintsPrivacy.ReadCdpResult(json, core.Settings.UserAgent,ProfilePrivacy.Allows(config,PrivacyException.SharedWorkers));
            diagnostic?.Invoke(json);
            if (result.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(result.Detail);
            if (AdditionalFingerprintPrivacy.IsEnabled(config.GraphicsPolicy))
            {
                if(MathImplementationPrivacy.IsEnabled(config)) {
                    var math=await core.ExecuteScriptAsync(MathImplementationPrivacy.EvaluationScript);
                    diagnostic?.Invoke("Native V8 pow bootstrap: "+math);
                    if(MathImplementationPrivacy.ReadResult(math)!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Эталонная реализация Math.pow не подтверждена.");
                }
                var residual=await core.ExecuteScriptAsync(ResidualFingerprintPrivacy.EvaluationScript);
                diagnostic?.Invoke("Residual privacy secure bootstrap: "+residual);
                var residualResult=ResidualFingerprintPrivacy.ReadResult(residual,config.PrivacyExceptions);
                if(residualResult.Outcome!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException(residualResult.Detail);
                var standardCdp = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {expression=StandardFingerprintPrivacy.EvaluationScript,awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(10));
                using var standardDocument = JsonDocument.Parse(standardCdp);
                if (standardDocument.RootElement.TryGetProperty("exceptionDetails",out _)) throw new InvalidOperationException("Проверка стандартных параметров не выполнена.");
                var standard = standardDocument.RootElement.GetProperty("result").GetProperty("value").GetRawText();
                diagnostic?.Invoke("Native document defaults bootstrap: " + standard);
                var standardResult = StandardFingerprintPrivacy.ReadResult(standard,ProfilePrivacy.Allows(config,PrivacyException.LocalFonts));
                if (standardResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(standardResult.Detail);
                var additionalCdp = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new {expression=AdditionalFingerprintPrivacy.EvaluationScript,awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(10));
                using var additionalDocument = JsonDocument.Parse(additionalCdp);
                if(additionalDocument.RootElement.TryGetProperty("exceptionDetails",out _)) throw new InvalidOperationException("Дополнительная проверка не выполнена.");
                var additional = additionalDocument.RootElement.GetProperty("result").GetProperty("value").GetRawText();
                diagnostic?.Invoke("Additional privacy secure bootstrap: " + additional);
                var additionalResult = AdditionalFingerprintPrivacy.ReadResult(additional,exceptions:config.PrivacyExceptions);
                if(additionalResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(additionalResult.Detail);
            }
            if (ComputePressurePrivacy.IsEnabled(config.GraphicsPolicy))
            {
                var pressure = await core.ExecuteScriptAsync(ComputePressurePrivacy.EvaluationScript);
                diagnostic?.Invoke("Compute Pressure secure bootstrap: " + pressure);
                var pressureResult = ComputePressurePrivacy.ReadResult(pressure);
                if (pressureResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(pressureResult.Detail);
            }
            if (HardwareDevicesPrivacy.IsEnabled(config.GraphicsPolicy))
            {
                var devices = await core.ExecuteScriptAsync(HardwareDevicesPrivacy.EvaluationScript);
                diagnostic?.Invoke("Hardware devices secure bootstrap: " + devices);
                var deviceResult = HardwareDevicesPrivacy.ReadResult(devices);
                if (deviceResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(deviceResult.Detail);
            }
            if (HardwareConcurrencyPrivacy.IsEnabled(config.GraphicsPolicy))
            {
                var cpu = await core.ExecuteScriptAsync(HardwareConcurrencyPrivacy.EvaluationScript);
                diagnostic?.Invoke("CPU secure bootstrap: " + cpu);
                var cpuResult = HardwareConcurrencyPrivacy.ReadResult(cpu,ExpectedCpu(core));
                if (cpuResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(cpuResult.Detail);
            }
            if (FontAccessPrivacy.IsEnabled(config))
            {
                var fonts = await core.ExecuteScriptAsync(FontAccessPrivacy.EvaluationScript);
                diagnostic?.Invoke("Local Font Access secure bootstrap: " + fonts);
                var fontResult = FontAccessPrivacy.ReadResult(fonts);
                if (fontResult.Outcome != GraphicsReadbackOutcome.Verified) throw new InvalidOperationException(fontResult.Detail);
            }
        }
        catch (Exception e) { throw new InvalidOperationException((FontAccessPrivacy.IsEnabled(config)
            ? "Ограничения UA Client Hints / Local Font Access / CPU / аппаратных API / Compute Pressure / стандартных CSS-параметров не подтверждены; открытие заблокировано. "
            : "Ограничение UA Client Hints не подтверждено; открытие заблокировано. ") + e.Message, e); }
        finally
        {
            core.NavigationCompleted -= Completed;
            // Keep the origin-local response filter for this controller's lifetime: late favicon/subresource
            // requests must also stay local after navigation completion. Controller disposal releases handlers.
        }
    }

}
