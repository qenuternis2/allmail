using System.Text.Json;
using Microsoft.Web.WebView2.Core;

internal static class PerformanceTimelineSmoke
{
    // Test-only workload. Production diagnostics never generate frames or CPU benchmarks.
    private const string Probe="""
        new Promise((resolve,reject)=>{
          if(!PerformanceObserver.supportedEntryTypes.includes('long-animation-frame')){reject(Error('LoAF positive control unavailable'));return;}
          const keys=['startTime','duration','renderStart','styleAndLayoutStart','firstUIEventTimestamp','blockingDuration','styleDuration','layoutDuration','paintTime','presentationTime','executionStart','forcedStyleAndLayoutDuration','forcedStyleDuration','forcedLayoutDuration','pauseDuration'];
          const read=entry=>Object.fromEntries(keys.filter(k=>k in entry).map(k=>[k,entry[k]]));
          const node=document.createElement('div');node.textContent='Native timeline test';document.body.appendChild(node);
          let raf;const timeout=setTimeout(()=>finish(Error('No real long frame with scripts')),4000);
          const observer=new PerformanceObserver(list=>{
            const entry=list.getEntries().find(e=>e.duration>=100&&e.scripts.length>0);
            if(!entry)return;
            const result={direct:read(entry),scripts:entry.scripts.map(read),json:entry.toJSON(),
              scriptJson:entry.scripts.map(e=>e.toJSON()),paint:performance.getEntriesByType('paint').map(e=>({direct:read(e),json:e.toJSON()})),
              nativeObserver:Function.prototype.toString.call(PerformanceObserver).includes('[native code]')};
            finish(null,result);
          });
          function finish(error,result){clearTimeout(timeout);observer.disconnect();if(raf!==undefined)cancelAnimationFrame(raf);node.remove();if(error)reject(error);else resolve(result);}
          observer.observe({type:'long-animation-frame'});
          raf=requestAnimationFrame(function allmailTimelineWorkload(){
            const end=Date.now()+220;
            while(Date.now()<end){node.style.width=(100+(node.offsetWidth%17))+'px';void node.offsetHeight;}
          });
        })
        """;
    internal static async Task CheckAsync(CoreWebView2 core,bool coarsened,string label)
    {
        using var result=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new{expression=Probe,awaitPromise=true,returnByValue=true})).WaitAsync(TimeSpan.FromSeconds(8)));
        if(result.RootElement.TryGetProperty("exceptionDetails",out var error))throw new InvalidOperationException("Native timeline evaluation failed: "+error);
        var o=result.RootElement.GetProperty("result").GetProperty("value");
        if(!o.GetProperty("nativeObserver").GetBoolean()||o.GetProperty("scripts").GetArrayLength()<1||o.GetProperty("json").GetProperty("scripts").GetArrayLength()<1
            ||!o.GetProperty("scripts").EnumerateArray().Any(s=>s.GetProperty("executionStart").GetDouble()>0))
            throw new InvalidOperationException("Real LoAF/script positive control missing: "+o);
        var fields=new HashSet<string>{"startTime","duration","renderStart","styleAndLayoutStart","firstUIEventTimestamp","blockingDuration","styleDuration","layoutDuration","paintTime","presentationTime","executionStart","forcedStyleAndLayoutDuration","forcedStyleDuration","forcedLayoutDuration","pauseDuration"};
        var timestamps=new List<double>();
        void Visit(JsonElement element)
        {
            if(element.ValueKind==JsonValueKind.Array){foreach(var item in element.EnumerateArray())Visit(item);}
            else if(element.ValueKind==JsonValueKind.Object)foreach(var p in element.EnumerateObject())
            {
                if(fields.Contains(p.Name)&&p.Value.ValueKind==JsonValueKind.Number)timestamps.Add(p.Value.GetDouble());
                else if(p.Name!="detail")Visit(p.Value);
            }
        }
        Visit(o);
        if(timestamps.Count<20 || (coarsened ? timestamps.Any(v=>!double.IsFinite(v)||v%100!=0) : !timestamps.Any(v=>v%100!=0)))
            throw new InvalidOperationException("Supplemental timeline precision mismatch: "+label+" "+o);
        Console.WriteLine("PASS: supplemental timeline "+label+"; coarsened="+coarsened+"; real LoAF getters/nested scripts/JSON, native observer: "+o);
    }
}
