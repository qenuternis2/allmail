using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

internal static class MathImplementationSmoke
{
    public static async Task RunAsync(Window window,string root)
    {
        JsonElement? portable=null,standard=null;
        foreach(var allowed in new[]{false,true})
        {
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"native-pow-"+allowed),new(){AdditionalBrowserArguments=BrowserArguments.Build(null,graphics:GraphicsPolicy.StrictFingerprintExperimental,exceptions:allowed?PrivacyException.NativeMath:PrivacyException.None),ExclusiveUserDataFolderAccess=true});
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
            try
            {
                using var view=new WebView2();window.Content=view;await view.EnsureCoreWebView2Async(environment);
                using var json=JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync(MathImplementationPrivacy.EvaluationScript));var o=json.RootElement.Clone();
                if(o.GetProperty("status").GetString()!="Observed"||!o.GetProperty("native").GetBoolean())throw new InvalidOperationException("Native pow control unavailable: "+o);
                if(allowed)standard=o;
                else {portable=o;if(MathImplementationPrivacy.ReadResult(o.GetRawText())!=GraphicsReadbackOutcome.Verified)throw new InvalidOperationException("Native LLVM pow reference failed: "+o);}
                Console.WriteLine("Native Math.pow control "+(allowed?"standard":"portable")+": "+o);
            }
            finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        }
        if(portable!.Value.GetProperty("values").GetRawText()==standard!.Value.GetProperty("values").GetRawText())throw new InvalidOperationException("Math.pow flag effect unproven: portable and standard controls identical.");
        Console.WriteLine("PASS: native Math.pow flag positive control; 16 independent LLVM reference vectors; standard path differs, functions native in both environments.");
    }
}
