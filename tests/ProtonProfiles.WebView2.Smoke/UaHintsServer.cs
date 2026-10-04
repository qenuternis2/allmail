using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ProtonProfiles.Core.Privacy;

// Actual loopback HTTP receiver: response echo comes from received headers, not host-side guesses.
internal sealed class UaHintsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private volatile bool _stopping;
    public string Uri { get; }
    private const string AcceptHints = "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform, Sec-CH-UA-Arch, Sec-CH-UA-Bitness, Sec-CH-UA-Model, Sec-CH-UA-Platform-Version, Sec-CH-UA-Full-Version-List, Sec-CH-UA-Full-Version, Sec-CH-UA-WoW64, Sec-CH-UA-Form-Factors";
    public UaHintsServer()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Uri = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Uri); _listener.Start();
        _loop = Task.Run(ServeAsync);
    }
    private async Task ServeAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                var path = context.Request.Url!.AbsolutePath;
                string body;
                if (path == "/echo")
                {
                    body = JsonSerializer.Serialize(context.Request.Headers.AllKeys.Where(k => k is not null)
                        .ToDictionary(k => k!, k => context.Request.Headers[k]!));
                    context.Response.ContentType = "application/json";
                }
                else if (path == "/dedicated.js" || path == "/shared.js" || path == "/service.js")
                {
                    // Capture UAData at the start of the worker script, before handlers/activation:
                    // checking only a later message could hide a startup race in native preparation.
                    body = UserAgentHintsPrivacy.ObservationScript + "\n" + HardwareConcurrencyPrivacy.ObservationScript + "\n" + HardwareDevicesPrivacy.ObservationScript + "\n" + ComputePressurePrivacy.ObservationScript + "\nconst initialAdditional=collectAdditionalFingerprintObservation(); const initialPressure=collectComputePressureObservation(); const initialDevices=collectHardwareDevicesObservation(); const initialCpu=collectCpuObservation(); const initialObservation=collectUaHintsObservation();\nasync function measure() { return {observation:await initialObservation, cpu:initialCpu, hardwareDevices:initialDevices, computePressure:initialPressure, additionalPrivacy:await initialAdditional, headers:await fetch('/echo').then(r=>r.json())}; }\n";
                    body += path switch {
                        "/dedicated.js" => "onmessage=()=>measure().then(value=>postMessage(value),e=>postMessage({error:String(e)}));",
                        "/shared.js" => "onconnect=e=>{const p=e.ports[0];p.onmessage=()=>measure().then(value=>p.postMessage(value),e=>p.postMessage({error:String(e)}));p.start();};",
                        _ => "oninstall=e=>e.waitUntil(skipWaiting());onactivate=e=>e.waitUntil(clients.claim());onmessage=e=>e.waitUntil(measure().then(value=>e.ports[0].postMessage(value),err=>e.ports[0].postMessage({error:String(err)})));",
                    };
                    context.Response.ContentType = "text/javascript";
                }
                else
                {
                    body = "<!doctype html><meta charset=utf-8><title>Local UA Client Hints control</title><script>" + UserAgentHintsPrivacy.ObservationScript + "\n" + HardwareConcurrencyPrivacy.ObservationScript + "\n" + HardwareDevicesPrivacy.ObservationScript + "\n" + ComputePressurePrivacy.ObservationScript + "\n" + AdditionalFingerprintPrivacy.ObservationScript + "\n" + """
                    (async()=>{
                      const request = (target, transfer=[]) => new Promise((resolve,reject)=>{
                        const port = target.port || target;
                        port.onmessage=e=>resolve(e.data);port.onmessageerror=()=>reject(new Error('message error'));
                        port.start?.();target.postMessage ? target.postMessage('observe',transfer) : port.postMessage('observe');
                      });
                      let worker, shared, registration;
                      try {
                        const progress = stage => chrome.webview.postMessage(JSON.stringify({progress:stage}));
                        const main={additionalPrivacy:await collectAdditionalFingerprintObservation(),computePressure:collectComputePressureObservation(),hardwareDevices:collectHardwareDevicesObservation(),cpu:collectCpuObservation(),observation:await collectUaHintsObservation(),headers:await fetch('/echo').then(r=>r.json())};progress('main observed');
                        worker=new Worker('/dedicated.js'); const dedicated=await request(worker);progress('dedicated observed');
                        let sharedResult={status:'NotApplicable',constructorAvailable:typeof SharedWorker!=='undefined'};
                        if (sharedResult.constructorAvailable) {shared=new SharedWorker('/shared.js');sharedResult=await request(shared);progress('shared observed');}
                        progress('service registering');registration=await navigator.serviceWorker.register('/service.js');await navigator.serviceWorker.ready;progress('service ready');
                        const channel=new MessageChannel();
                        const servicePromise=new Promise(resolve=>channel.port1.onmessage=e=>resolve(e.data));
                        registration.active.postMessage('observe',[channel.port2]);
                        const service=await servicePromise;channel.port1.close();
                        chrome.webview.postMessage(JSON.stringify({main,dedicated,shared:sharedResult,service}));
                      } finally {worker?.terminate();shared?.port.close();if(registration) await registration.unregister();}
                    })().catch(e=>chrome.webview.postMessage(JSON.stringify({error:String(e)})));
                    </script>
                    """;
                    context.Response.ContentType = "text/html; charset=utf-8";
                }
                context.Response.Headers["Accept-CH"] = AcceptHints;
                context.Response.Headers["Cache-Control"] = "no-store";
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
        catch (HttpListenerException) when (_stopping) { }
        catch (ObjectDisposedException) { }
    }
    public void Dispose() { _stopping = true; _listener.Stop(); _listener.Close(); _loop.GetAwaiter().GetResult(); }
}
