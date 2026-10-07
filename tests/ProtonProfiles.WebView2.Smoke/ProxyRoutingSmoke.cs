using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using ProtonProfiles.App.Browser;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;

internal static class ProxyRoutingSmoke
{
    public static async Task RunAsync(Window window,string root)
    {
        using var ipv4=new Receiver(IPAddress.Loopback);
        using var ipv6=new Receiver(IPAddress.IPv6Loopback);
        using var tls=new Receiver(IPAddress.Loopback,tls:true);
        foreach(var address in new[]{IPAddress.Loopback,IPAddress.IPv6Loopback})
        {
            using var proxy=new Proxy(address,tls.Port);
            if(!ProxyEndpoint.TryParse(proxy.Uri,out var endpoint,out var error))throw new InvalidOperationException(error);
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"proxy-"+address.AddressFamily),new()
                {AdditionalBrowserArguments=BrowserArguments.Build(endpoint,graphics:GraphicsPolicy.StrictFingerprintExperimental),ExclusiveUserDataFolderAccess=true});
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
            try
            {
                using var view=new WebView2();window.Content=view;await view.EnsureCoreWebView2Async(environment);var core=view.CoreWebView2;
                var targets=new[] {$"http://127.0.0.1:{ipv4.Port}/ipv4",$"http://[::1]:{ipv6.Port}/ipv6","http://target.proxy-fixture.invalid/dns",$"https://proxy-target.invalid:{tls.Port}/https"};
                foreach(var url in targets)
                {
                    if(!await NavigateAsync(core,url))throw new InvalidOperationException("Proxy fixture navigation failed: "+url);
                    if(await core.ExecuteScriptAsync("document.body.textContent.includes('proxied-fixture')")!="true")throw new InvalidOperationException("Proxy receiver marker missing.");
                }
                if(ipv4.Requests!=0 || ipv6.Requests!=0)throw new InvalidOperationException("Loopback bypass made a direct request.");
                if(tls.Requests==0 || !proxy.Targets.Any(t=>t.StartsWith("CONNECT proxy-target.invalid:",StringComparison.Ordinal)))throw new InvalidOperationException("CONNECT tunnel was not exercised.");
                foreach(var target in targets.Take(3))
                    if(!proxy.Targets.Any(t=>t=="GET "+target))throw new InvalidOperationException("Proxy did not receive target URL: "+target);
                const string webSocketScript="""
                    new Promise(resolve=>{const s=new WebSocket(URL_VALUE);const timer=setTimeout(()=>{s.close();resolve('timeout');},5000);
                    s.onopen=()=>s.send('wss-fixture');s.onmessage=e=>{clearTimeout(timer);s.close();resolve(e.data);};s.onerror=()=>{clearTimeout(timer);resolve('error');};})
                    """;
                var wssUrl=$"wss://proxy-target.invalid:{tls.Port}/wss";
                async Task<string?> Wss(string url)=>JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(webSocketScript.Replace("URL_VALUE",JsonSerializer.Serialize(url),StringComparison.Ordinal)));
                if(await Wss(wssUrl)!="wss-fixture")throw new InvalidOperationException("WSS CONNECT echo failed.");
                await ProxyStartupCheck.NavigateAsync(core,"http://target.proxy-fixture.invalid/challenge",CancellationToken.None,true);
                if(core.Source!="http://target.proxy-fixture.invalid/challenge")throw new InvalidOperationException("HTTP403 challenge rejected by connectivity check.");
                await ProxyStartupCheck.NavigateAsync(core,targets[0],CancellationToken.None,true);
                var tlsBefore=tls.Requests;
                proxy.Stop();
                foreach(var url in targets)
                    if(await NavigateAsync(core,url+"?proxy-down="+Guid.NewGuid().ToString("N"),TimeSpan.FromSeconds(45)))throw new InvalidOperationException("Stopped proxy silently opened destination.");
                if(await Wss(wssUrl+"?stopped=1")=="wss-fixture")throw new InvalidOperationException("Stopped proxy silently opened WSS destination.");
                var rejected=false;
                try {await ProxyStartupCheck.NavigateAsync(core,targets[0]+"?cached=1",CancellationToken.None,true);}
                catch(InvalidOperationException) {rejected=true;}
                if(!rejected)throw new InvalidOperationException("Startup check accepted unavailable proxy.");
                await Task.Delay(100);
                if(ipv4.Requests!=0 || ipv6.Requests!=0 || tls.Requests!=tlsBefore)throw new InvalidOperationException("Direct traffic occurred after proxy failure.");
                Console.WriteLine("PASS: strict proxy "+address.AddressFamily+"; HTTP IPv4/IPv6 loopback through proxy, unresolvable target hostname via proxy, HTTPS/WSS CONNECT with trusted fixture TLS; startup connectivity accepts HTTP403 challenge and rejects stopped proxy; stopped proxy blocks every target; direct receivers zero. Targets: "+JsonSerializer.Serialize(proxy.Targets.ToArray()));
            }
            finally {window.Content=null;await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        }
    }
    private static async Task<bool> NavigateAsync(CoreWebView2 core,string url,TimeSpan? timeout=null)
    {
        var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed=Stopwatch.StartNew();ulong? navigationId=null;var expected=new Uri(url);
        void Start(object? sender,CoreWebView2NavigationStartingEventArgs e)
        {
            if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var actual)&&actual==expected)navigationId=e.NavigationId;
        }
        void Complete(object? sender,CoreWebView2NavigationCompletedEventArgs e)
        {
            if(e.NavigationId!=navigationId)return;
            if(!e.IsSuccess)Console.WriteLine("Proxy fixture navigation status: "+e.WebErrorStatus+" after "+elapsed.ElapsedMilliseconds+" ms; "+url);
            result.TrySetResult(e.IsSuccess);
        }
        core.NavigationStarting+=Start;core.NavigationCompleted+=Complete;
        try {core.Navigate(url);return await result.Task.WaitAsync(timeout??TimeSpan.FromSeconds(15));}
        catch(TimeoutException e) {core.Stop();throw new TimeoutException("Proxy fixture navigation did not complete: "+url,e);}
        finally {core.NavigationStarting-=Start;core.NavigationCompleted-=Complete;}
    }
    private abstract class Server:IDisposable
    {
        protected readonly TcpListener Listener;
        private readonly ConcurrentBag<TcpClient> _clients=[];
        private readonly CancellationTokenSource _stop=new();
        private readonly Task _loop;
        public int Port=>((IPEndPoint)Listener.LocalEndpoint).Port;
        protected Server(IPAddress address)
        {
            Listener=new TcpListener(address,0);Listener.Start();
            _loop=Task.Run(async()=>{
                try {
                    while(!_stop.IsCancellationRequested) {
                        var client=await Listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(client);
                        _=Task.Run(async()=>{using(client)try {await HandleAsync(client);}catch(Exception e) when(e is IOException or SocketException or ObjectDisposedException or System.Security.Authentication.AuthenticationException) {if(!_stop.IsCancellationRequested)Console.WriteLine("Proxy fixture transport "+GetType().Name+": "+e.Message);} });
                    }
                }catch(OperationCanceledException) {}catch(SocketException) when(_stop.IsCancellationRequested) {}
                catch(ObjectDisposedException) when(_stop.IsCancellationRequested) {}
            });
        }
        protected abstract Task HandleAsync(TcpClient client);
        public void Stop(){if(_stop.IsCancellationRequested)return;_stop.Cancel();Listener.Stop();foreach(var client in _clients)client.Dispose();}
        public virtual void Dispose(){Stop();_loop.GetAwaiter().GetResult();_stop.Dispose();}
        protected static async Task<string> ReadHeadersAsync(Stream stream)
        {
            var bytes=new List<byte>();var b=new byte[1];
            while(bytes.Count<32768 && await stream.ReadAsync(b)>0){bytes.Add(b[0]);if(bytes.Count>=4 && bytes[^4]==13 && bytes[^3]==10 && bytes[^2]==13 && bytes[^1]==10)return Encoding.ASCII.GetString(bytes.ToArray());}
            return "";
        }
        protected static async Task RespondAsync(Stream stream,bool challenge=false)
        {
            const string body="<!doctype html><body>proxied-fixture</body>";
            var bytes=Encoding.ASCII.GetBytes("HTTP/1.1 "+(challenge?"403 Forbidden":"200 OK")+"\r\nContent-Type: text/html\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"+body);
            await stream.WriteAsync(bytes);await stream.FlushAsync();
        }
    }
    private sealed class Receiver:Server
    {
        private readonly X509Certificate2? _certificate;
        private int _requests;
        public int Requests=>Volatile.Read(ref _requests);
        public Receiver(IPAddress address,bool tls=false):base(address)
        {
            if(tls) {
                using var rsa=RSA.Create(2048);
                var request=new CertificateRequest("CN=proxy-target.invalid",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
                var san=new SubjectAlternativeNameBuilder();san.AddDnsName("proxy-target.invalid");request.CertificateExtensions.Add(san.Build());
                using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
                // Windows SChannel requires an imported private key rather than the ephemeral
                // RSA handle returned by CreateSelfSigned. Certificate remains fixture-only.
                _certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
                using var trust=new X509Store(StoreName.Root,StoreLocation.CurrentUser);trust.Open(OpenFlags.ReadWrite);
                trust.Add(_certificate);
            }
        }
        protected override async Task HandleAsync(TcpClient client)
        {
            var network=client.GetStream();
            if(_certificate is not null) {
                using var ssl=new SslStream(network,false);await ssl.AuthenticateAsServerAsync(_certificate);
                var headers=await ReadHeadersAsync(ssl);if(headers.Length==0)return;Interlocked.Increment(ref _requests);
                if(headers.StartsWith("GET /wss",StringComparison.Ordinal)) {
                    var key=headers.Split("\r\n").Single(h=>h.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase)).Split(':',2)[1].Trim();
                    var accept=Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\n\r\n"));
                    using var socket=WebSocket.CreateFromStream(ssl,true,null,TimeSpan.FromSeconds(20));var buffer=new byte[256];
                    var received=await socket.ReceiveAsync(buffer,CancellationToken.None);
                    await socket.SendAsync(buffer.AsMemory(0,received.Count),WebSocketMessageType.Text,true,CancellationToken.None);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"fixture complete",CancellationToken.None);
                } else await RespondAsync(ssl);
            } else {if((await ReadHeadersAsync(network)).Length==0)return;Interlocked.Increment(ref _requests);await RespondAsync(network);}
        }
        public override void Dispose(){base.Dispose();if(_certificate is not null) {
            using var trust=new X509Store(StoreName.Root,StoreLocation.CurrentUser);trust.Open(OpenFlags.ReadWrite);trust.Remove(_certificate);_certificate.Dispose();}}
    }
    private sealed class Proxy:Server
    {
        private readonly int _tlsPort;
        public ConcurrentBag<string> Targets {get;}=[];
        public string Uri {get;}
        public Proxy(IPAddress address,int tlsPort):base(address){_tlsPort=tlsPort;Uri="http://"+(address.AddressFamily==AddressFamily.InterNetworkV6?"["+address+"]":address.ToString())+":"+Port;}
        protected override async Task HandleAsync(TcpClient client)
        {
            var downstream=client.GetStream();var headers=await ReadHeadersAsync(downstream);if(headers.Length==0)return;
            var first=headers.Split("\r\n")[0].Split(' ');Targets.Add(first[0]+" "+first[1]);
            if(first[0]!="CONNECT"){await RespondAsync(downstream,first[1].Contains("/challenge",StringComparison.Ordinal));return;}
            using var upstream=new TcpClient();await upstream.ConnectAsync(IPAddress.Loopback,_tlsPort);
            await downstream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
            var stream=upstream.GetStream();await Task.WhenAny(downstream.CopyToAsync(stream),stream.CopyToAsync(downstream));
        }
    }
}
