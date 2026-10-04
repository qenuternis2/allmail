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

internal static class ProtonCompatibilityDiagnostics
{
    private const string ErrorObserver="(()=>{const errors=[];Object.defineProperty(globalThis,'__allmailPublicErrors',{value:errors});const clean=v=>String(v??'').replace(/https?:\\/\\/\\S+/g,'[url]').slice(0,240);addEventListener('error',e=>{if(errors.length<8){let file='';try{file=new URL(e.filename).pathname.split('/').pop()}catch{}errors.push({kind:'error',name:e.error?.name??null,message:clean(e.message),file,line:e.lineno,column:e.colno})}});addEventListener('unhandledrejection',e=>{if(errors.length<8)errors.push({kind:'rejection',name:e.reason?.name??null,message:clean(e.reason?.message??e.reason)})});})()";
    public static async Task RunAsync(Window window,string root)
    {
        var masks=new[]{PrivacyException.None,ProfilePrivacy.KnownExceptions}.Concat(Enum.GetValues<PrivacyException>().Where(v=>v!=PrivacyException.None).Select(v=>ProfilePrivacy.KnownExceptions&~v));
        foreach(var mask in masks)
        {
            var config=new ProfileConfig{Id=Guid.NewGuid(),DisplayName="Public compatibility diagnostic",GraphicsPolicy=GraphicsPolicy.StrictFingerprintExperimental,PrivacyExceptions=mask,BrowserTimeZoneId="Europe/Riga"};
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"public-proton-"+(long)mask),new(){AdditionalBrowserArguments=BrowserArguments.Build(null,graphics:config.GraphicsPolicy,exceptions:mask),ExclusiveUserDataFolderAccess=true});
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
            try
            {
                var grid=new Grid();window.Content=grid;using var view=new WebView2();grid.Children.Add(view);await view.EnsureCoreWebView2Async(environment);var core=view.CoreWebView2;
                core.PermissionRequested+=(_,e)=>{e.Handled=true;e.SavesInProfile=false;e.State=CoreWebView2PermissionState.Deny;};
                string? failure=null;await UserAgentHintsBootstrap.ApplyAsync(core,config,onFailure:reason=>{failure=reason;return Task.CompletedTask;});
                await UserAgentHintsBootstrap.VerifyAsync(core,environment,config,true);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcPageGuard.Script);
                if(AudioPageGuard.IsEnabled(config))await core.AddScriptToExecuteOnDocumentCreatedAsync(AudioPageGuard.Script);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ErrorObserver);
                try
                {
                    var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);void Complete(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>ready.TrySetResult(e.IsSuccess);
                    core.NavigationCompleted+=Complete;try{core.Navigate("https://mail.proton.me/");if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)))throw new InvalidOperationException("Navigation failed.");}finally{core.NavigationCompleted-=Complete;}
                    JsonElement snapshot=default;JsonElement errors=default;
                    for(var attempt=0;attempt<10;attempt++){
                        await Task.Delay(1000);
                        using var result=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new{expression=FingerprintProbePage.WebCryptoEvaluationScript,awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(5)));
                        snapshot=result.RootElement.GetProperty("result").GetProperty("value").Clone();if(snapshot.GetProperty("protonSupportedBrowser").ValueKind==JsonValueKind.Number)break;
                    }
                    await Task.Delay(500);
                    using(var result=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate","{\"expression\":\"globalThis.__allmailPublicErrors??[]\",\"returnByValue\":true}")))errors=result.RootElement.GetProperty("result").GetProperty("value").Clone();
                    Console.WriteLine("PUBLIC_PROTON_DIAGNOSTIC "+JsonSerializer.Serialize(new{mask=(long)mask,exceptions=ProfilePrivacy.Names(mask),status="Observed",snapshot,errors,protocolFailure=failure is not null}));
                }
                catch(Exception e){Console.WriteLine("PUBLIC_PROTON_DIAGNOSTIC "+JsonSerializer.Serialize(new{mask=(long)mask,exceptions=ProfilePrivacy.Names(mask),status="NotPerformed",errorName=e.GetType().Name}));}
            }
            finally{window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        }
    }
}
