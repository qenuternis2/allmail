using System.IO;
using System.Net;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.App.Browser;

/// <summary>Only our expression runs on the initial blank page. Fetch uses this controller's proxy and authentication.</summary>
internal static class AutoTimeZoneBootstrap
{
    internal static async Task<IPAddress[]> DiscoverAsync(CoreWebView2 core, CancellationToken cancellationToken = default,
        string ipv4Url = "https://api.ipify.org?format=json", string ipv6Url = "https://api6.ipify.org?format=json")
    {
        if (core.Source != "about:blank") throw new InvalidOperationException("Автоопределение должно выполняться до открытия сайта.");
        var expression = $$"""
            (async()=>await Promise.all({{JsonSerializer.Serialize(new[] { ipv4Url, ipv6Url })}}.map(async(url)=>{
              const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),8000);
              try {
                const response=await fetch(url,{signal:abort.signal,credentials:'omit',cache:'no-store',redirect:'error',referrerPolicy:'no-referrer'});
                if(!response.ok)return null;
                const stream=response.body.getReader(),decoder=new TextDecoder();let text='',size=0;
                while(true){const {done,value}=await stream.read();if(done)break;size+=value.length;if(size>1024){await stream.cancel();return null;}text+=decoder.decode(value,{stream:true});}
                text+=decoder.decode();const ip=JSON.parse(text).ip;
                return typeof ip==='string'&&ip.length<=45?ip:null;
              }catch{return null;}finally{clearTimeout(timer);}
            })))()
            """;
        await core.CallDevToolsProtocolMethodAsync("Network.setBypassServiceWorker", "{\"bypass\":true}");
        var raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        { expression, awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.TryGetProperty("exceptionDetails", out _)
            || !document.RootElement.GetProperty("result").TryGetProperty("value", out var values)
            || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 2)
            throw new InvalidDataException("Не удалось определить IP через браузер профиля.");
        var addresses = new List<IPAddress>();
        for (var index = 0; index < 2; index++)
        {
            var value = values[index];
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String || !IPAddress.TryParse(value.GetString(), out var ip)
                || !GeoIpTimeZoneDatabase.IsPublic(ip)
                || ip.AddressFamily != (index == 0 ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6))
                throw new InvalidDataException("Сервис определения IP вернул недопустимый адрес.");
            addresses.Add(ip);
        }
        if (addresses.Count == 0) throw new InvalidDataException("Не удалось получить IP выхода. Проверьте соединение и прокси; автоматический пояс не применён.");
        return addresses.ToArray();
    }
}
