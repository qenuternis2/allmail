using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

internal static class TimeZoneSmoke
{
    public static async Task RunAsync(Window window,string root)
    {
        foreach(var policy in new[]{GraphicsPolicy.RuntimeDefault,GraphicsPolicy.StrictFingerprintExperimental})
        {
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"timezone-"+policy),new(){
                AdditionalBrowserArguments=BrowserArguments.Build(null,graphics:policy)+" --site-per-process",ExclusiveUserDataFolderAccess=true});
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
            try
            {
                var grid=new Grid();window.Content=grid;
                using var main=new WebView2();using var child=new WebView2();grid.Children.Add(main);grid.Children.Add(child);
                string? zone=null;
                foreach(var (view,label) in new[]{(main,"main"),(child,"child")})
                {
                    await view.EnsureCoreWebView2Async(environment);var core=view.CoreWebView2;
                    foreach(var host in new[]{"allmail-smoke.test","allmail-frame.test"})
                        core.SetVirtualHostNameToFolderMapping(host,AppContext.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
                    if(zone is null) {
                        var native=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("Intl.DateTimeFormat().resolvedOptions().timeZone"));
                        zone=native=="Europe/Riga"?"America/New_York":"Europe/Riga";
                        await core.CallDevToolsProtocolMethodAsync("Emulation.setTimezoneOverride",JsonSerializer.Serialize(new{timezoneId=zone}));
                        using var control=JsonDocument.Parse(await ObserveAsync(core));
                        if(control.RootElement.GetProperty("main").GetProperty("timeZone").GetString()!=zone
                            ||control.RootElement.GetProperty("cross").GetProperty("timeZone").GetString()==zone)
                            throw new InvalidOperationException("Timezone root-only negative control did not reproduce the OOP iframe gap: "+control.RootElement);
                        Console.WriteLine("PASS: root-only timezone negative control; cross-origin first script retains host zone: "+control.RootElement);
                        await NavigateBlankAsync(core);
                    }
                    var config=new ProfileConfig{Id=Guid.NewGuid(),DisplayName="timezone "+label,GraphicsPolicy=policy,BrowserTimeZoneId=zone};
                    var iframePrepared=0;string? failure=null;var ua=core.Settings.UserAgent;
                    await UserAgentHintsBootstrap.ApplyAsync(core,config,onFailure:reason=>{failure=reason;return Task.CompletedTask;},
                        diagnostic:message=>{if(message.StartsWith("Time zone target iframe:"))iframePrepared++;});
                    using var report=JsonDocument.Parse(await ObserveAsync(core));
                    var tz=TimeZoneInfo.FindSystemTimeZoneById(zone);
                    foreach(var scope in new[]{"main","same","cross","worker"}) {
                        var value=report.RootElement.GetProperty(scope);
                        if(value.GetProperty("timeZone").GetString()!=zone||!value.GetProperty("nativeDate").GetBoolean()||!value.GetProperty("nativeIntl").GetBoolean()
                            ||value.GetProperty("winter").GetInt32()!=-(int)tz.GetUtcOffset(new DateTimeOffset(2026,1,15,12,0,0,TimeSpan.Zero)).TotalMinutes
                            ||value.GetProperty("summer").GetInt32()!=-(int)tz.GetUtcOffset(new DateTimeOffset(2026,7,15,12,0,0,TimeSpan.Zero)).TotalMinutes)
                            throw new InvalidOperationException("Native timezone startup mismatch: "+scope+" "+value);
                    }
                    if(iframePrepared<1||failure is not null||core.Settings.UserAgent!=ua)throw new InvalidOperationException("Timezone setup missing OOP preparation or changed UA: "+failure);
                    Console.WriteLine("PASS: native timezone startup "+policy+" "+label+"; OOP iframe preparation observed; main/same/cross/dedicated first script, winter/summer offsets, native Date/Intl and UA retained: "+report.RootElement);
                }
            }
            finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        }
    }
    private static async Task<string> ObserveAsync(CoreWebView2 core)
    {
        var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender,CoreWebView2WebMessageReceivedEventArgs e){if(e.Source=="https://allmail-smoke.test/timezone.html")result.TrySetResult(e.TryGetWebMessageAsString());}
        core.WebMessageReceived+=Received;
        try {core.Navigate("https://allmail-smoke.test/timezone.html");return await result.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        finally {core.WebMessageReceived-=Received;}
    }
    private static async Task NavigateBlankAsync(CoreWebView2 core)
    {
        var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>result.TrySetResult(e.IsSuccess);
        core.NavigationCompleted+=Completed;
        try {core.Navigate("about:blank");if(!await result.Task.WaitAsync(TimeSpan.FromSeconds(10)))throw new InvalidOperationException("Timezone control cleanup failed.");}
        finally {core.NavigationCompleted-=Completed;}
    }
}
