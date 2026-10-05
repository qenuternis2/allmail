using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Privacy;

internal static class DisplayPostureSmoke
{
    internal static async Task PrepareFoldedAsync(CoreWebView2 core,string label,bool segmentsBlocked)
    {
        var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>ready.TrySetResult(e.IsSuccess);
        core.NavigationCompleted+=Completed;
        try {core.Navigate("https://allmail-smoke.test/header-control.html?display-posture");if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)))throw new InvalidOperationException("Display control navigation failed.");}
        finally {core.NavigationCompleted-=Completed;}
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDevicePostureOverride","{\"posture\":{\"type\":\"folded\"}}");
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDisplayFeaturesOverride","{\"features\":[{\"orientation\":\"vertical\",\"offset\":180,\"maskLength\":8}]}");
        JsonElement o=default;
        for(var attempt=0;attempt<15;attempt++) {
            o=await ObserveAsync(core);
            if(o.GetProperty("status").GetString()=="Observed"&&o.GetProperty("media").GetProperty("folded").GetBoolean()
                &&(!o.GetProperty("postureApiAvailable").GetBoolean()||o.GetProperty("postureType").GetString()=="folded"))break;
            await Task.Delay(50);
        }
        var css=o.GetProperty("css");var media=o.GetProperty("media");
        if(o.GetProperty("status").GetString()!="Observed"||!media.GetProperty("folded").GetBoolean()||css.GetProperty("posture").GetString()!="folded"
            ||o.GetProperty("postureApiAvailable").GetBoolean()&&(o.GetProperty("postureType").GetString()!="folded"||!o.GetProperty("nativePostureGetter").GetBoolean()))
            throw new InvalidOperationException("Folded native positive control missing: "+label+" "+o);
        if(segmentsBlocked) {
            if(new[]{"horizontalSingle","horizontalDouble","verticalSingle","verticalDouble"}.Any(k=>media.GetProperty(k).GetBoolean())||css.GetProperty("horizontal").GetInt32()!=0||css.GetProperty("vertical").GetInt32()!=0
                ||o.GetProperty("viewportApiAvailable").GetBoolean()||css.GetProperty("envSupported").GetBoolean()&&css.GetProperty("secondSegmentLeft").GetDouble()!=777)
                throw new InvalidOperationException("Forced split exposed despite native flag: "+label+" "+o);
        }else if(!media.GetProperty("horizontalDouble").GetBoolean()||css.GetProperty("horizontal").GetInt32()!=2||!css.GetProperty("envSupported").GetBoolean()
            ||Math.Abs(css.GetProperty("secondSegmentLeft").GetDouble()-188)>0.01||!o.GetProperty("viewportApiAvailable").GetBoolean()||o.GetProperty("segmentCount").GetInt32()!=2||!o.GetProperty("nativeSegmentsGetter").GetBoolean())
            throw new InvalidOperationException("Split native positive control missing: "+label+" "+o);
        Console.WriteLine("PASS: native display posture positive control "+label+"; forced folded/split; segmentsBlocked="+segmentsBlocked+"; CSS/media/env and native API: "+o);
    }
    internal static async Task ClearAsync(CoreWebView2 core)
    {
        await core.CallDevToolsProtocolMethodAsync("Emulation.clearDevicePostureOverride","{}");
        await core.CallDevToolsProtocolMethodAsync("Emulation.clearDisplayFeaturesOverride","{}");
    }
    private static async Task<JsonElement> ObserveAsync(CoreWebView2 core)
    {
        var script="(()=>{\n"+StandardFingerprintPrivacy.ObservationScript+"\nreturn collectDisplayPostureObservation();})()";
        using var result=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new{expression=script,returnByValue=true})));
        if(result.RootElement.TryGetProperty("exceptionDetails",out var error))throw new InvalidOperationException("Display observation failed: "+error);
        return result.RootElement.GetProperty("result").GetProperty("value").Clone();
    }
}
