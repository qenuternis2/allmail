using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;

namespace ProtonProfiles.Core.Tests;

public sealed class AuthenticatedProxyRelayTests
{
    private static readonly ProxyCredential Credential = new("fixture", "fixture-secret");
    private static string Authorization => "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:fixture-secret"));

    [Fact]
    public async Task Parallel_tunnels_retry_independently_and_pass_opaque_bytes()
    {
        var attempts = new ConcurrentDictionary<string,int>();
        using var proxy = new Server(async (stream, headers) => {
            Assert.Contains(Authorization, headers);
            Assert.DoesNotContain("browser-secret", headers);
            var target = headers.Split(' ')[1];
            if (attempts.AddOrUpdate(target,1,(_,count)=>count+1)==1)
            { await Reply(stream,"407 Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"fixture\"\r\n"); return; }
            await Reply(stream,"200 Connection Established");
            var payload = new byte[65537];
            await stream.ReadExactlyAsync(payload);
            await stream.WriteAsync(payload);
        });
        using var relay = new AuthenticatedProxyRelay(proxy.Endpoint,Credential,_=>throw new InvalidOperationException("Unexpected proxy rejection"));
        await Task.WhenAll(Enumerable.Range(0,6).Select(async i => {
            using var client = await Connect(relay);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target-{i}.invalid:443 HTTP/1.1\r\nProxy-Authorization: browser-secret\r\n\r\n"));
            Assert.Contains("200 Connection Established",await Headers(stream));
            var payload = new byte[65537]; Random.Shared.NextBytes(payload);
            await stream.WriteAsync(payload);
            var received = new byte[payload.Length]; await stream.ReadExactlyAsync(received);
            Assert.Equal(payload,received);
        })).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(6,attempts.Count);
        Assert.All(attempts.Values,count=>Assert.Equal(2,count));
        proxy.AssertHealthy();
    }

    [Fact]
    public async Task Rejected_credentials_are_bounded_and_do_not_exhaust_future_connections()
    {
        var calls=0; var problems=new ConcurrentBag<string>(); var reject=true;
        using var proxy=new Server(async(stream,_)=> {
            Interlocked.Increment(ref calls);
            await Reply(stream,reject?"407 Proxy Authentication Required":"200 Connection Established",
                reject?"Proxy-Authenticate: Basic realm=\"fixture\"\r\n":"");
        });
        using var relay=new AuthenticatedProxyRelay(proxy.Endpoint,Credential,problems.Add);
        using(var client=await Connect(relay))
        {
            await client.GetStream().WriteAsync("CONNECT rejected.invalid:443 HTTP/1.1\r\n\r\n"u8.ToArray());
            var response=await Headers(client.GetStream());
            Assert.Contains("502",response); Assert.DoesNotContain("Proxy-Authenticate",response); Assert.DoesNotContain("fixture-secret",response);
        }
        Assert.Equal(2,calls); Assert.Single(problems);
        reject=false;
        using(var client=await Connect(relay))
        {
            await client.GetStream().WriteAsync("CONNECT fresh.invalid:443 HTTP/1.1\r\n\r\n"u8.ToArray());
            Assert.Contains("200",await Headers(client.GetStream()));
        }
        Assert.Equal(3,calls); Assert.Single(problems); proxy.AssertHealthy();
    }

    [Fact]
    public async Task Http_post_body_is_forwarded_once_with_proxy_credentials_and_response_close()
    {
        var calls=0; var body=new byte[96001]; Random.Shared.NextBytes(body);
        using var proxy=new Server(async(stream,headers)=> {
            Interlocked.Increment(ref calls);
            Assert.StartsWith("POST http://target.invalid/upload HTTP/1.1",headers);
            Assert.Contains(Authorization,headers); Assert.Contains("Connection: close",headers);
            var received=new byte[body.Length];await stream.ReadExactlyAsync(received);Assert.Equal(body,received);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: keep-alive\r\n\r\ndone"u8.ToArray());
        });
        using var relay=new AuthenticatedProxyRelay(proxy.Endpoint,Credential,_=>throw new InvalidOperationException());
        using var client=await Connect(relay);var network=client.GetStream();
        await network.WriteAsync(Encoding.ASCII.GetBytes($"POST http://target.invalid/upload HTTP/1.1\r\nHost: target.invalid\r\nContent-Length: {body.Length}\r\n\r\n"));
        await network.WriteAsync(body);
        Assert.Contains("Connection: close",await Headers(network));
        var result=new byte[4]; await network.ReadExactlyAsync(result);Assert.Equal("done",Encoding.ASCII.GetString(result));
        Assert.Equal(1,calls);proxy.AssertHealthy();
    }

    [Theory]
    [InlineData("GET /relative HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://user:secret@target.invalid/ HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT target.invalid:443/path HTTP/1.1\r\n\r\n")]
    public async Task Malformed_or_credentialed_destinations_do_not_reach_upstream(string request)
    {
        var calls=0;using var proxy=new Server((_,_)=>{Interlocked.Increment(ref calls);return Task.CompletedTask;});
        using var relay=new AuthenticatedProxyRelay(proxy.Endpoint,Credential,_=>{});
        using var client=await Connect(relay);await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
        Assert.Contains("502",await Headers(client.GetStream()));Assert.Equal(0,calls);
    }

    [Fact]
    public async Task Unavailable_proxy_does_not_connect_destination_directly()
    {
        using var destination=new TcpListener(IPAddress.Loopback,0);destination.Start();
        using var unused=new TcpListener(IPAddress.Loopback,0);unused.Start();
        ProxyEndpoint.TryCreate("http","127.0.0.1",((IPEndPoint)unused.LocalEndpoint).Port,out var upstream,out _);unused.Stop();
        using var relay=new AuthenticatedProxyRelay(upstream!,Credential,_=>{});
        using var client=await Connect(relay);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port} HTTP/1.1\r\n\r\n"));
        Assert.Contains("502",await Headers(client.GetStream()));Assert.False(destination.Pending());
    }

    [Fact]
    public async Task Closing_relay_closes_live_tunnel_and_listener()
    {
        using var proxy=new Server(async(stream,_)=> {await Reply(stream,"200 Connection Established");var read=await stream.ReadAsync(new byte[1]);if(read!=0)throw new IOException("Unexpected tunnel bytes.");});
        using var relay=new AuthenticatedProxyRelay(proxy.Endpoint,Credential,_=>{});
        using var client=await Connect(relay);await client.GetStream().WriteAsync("CONNECT target.invalid:443 HTTP/1.1\r\n\r\n"u8.ToArray());
        Assert.Contains("200",await Headers(client.GetStream()));relay.Dispose();
        Assert.Equal(0,await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        using var denied=new TcpClient();await Assert.ThrowsAsync<SocketException>(()=>denied.ConnectAsync("127.0.0.1",relay.Endpoint.Port));
    }

    private static async Task<TcpClient> Connect(AuthenticatedProxyRelay relay)
    {var client=new TcpClient();await client.ConnectAsync("127.0.0.1",relay.Endpoint.Port);return client;}
    private static Task Reply(Stream stream,string status,string extra="") => stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\n"+extra+"\r\n")).AsTask();
    private static async Task<string> Headers(Stream stream)
    {
        var bytes=new List<byte>();var one=new byte[1];
        while(await stream.ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5))!=0)
        {bytes.Add(one[0]);if(bytes.Count>=4&&bytes[^4]==13&&bytes[^3]==10&&bytes[^2]==13&&bytes[^1]==10)return Encoding.Latin1.GetString(bytes.ToArray());}
        return Encoding.Latin1.GetString(bytes.ToArray());
    }
    private sealed class Server:IDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<TcpClient> _clients=[];
        private readonly ConcurrentBag<Exception> _errors=[];
        public ProxyEndpoint Endpoint {get;}
        public Server(Func<NetworkStream,string,Task> handle)
        {
            _listener.Start();ProxyEndpoint.TryCreate("http","127.0.0.1",((IPEndPoint)_listener.LocalEndpoint).Port,out var endpoint,out _);Endpoint=endpoint!;
            _=Task.Run(async()=> {
                try {while(!_stop.IsCancellationRequested) {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(client);
                    _=Task.Run(async()=> {using(client)try {var stream=client.GetStream();await handle(stream,await Headers(stream));}
                        catch(Exception e) {if(!_stop.IsCancellationRequested)_errors.Add(e);} });
                }}catch(Exception e) when(e is OperationCanceledException or SocketException or ObjectDisposedException) { }
            });
        }
        public void AssertHealthy()=>Assert.Empty(_errors);
        public void Dispose(){_stop.Cancel();_listener.Stop();foreach(var client in _clients)client.Dispose();}
    }
}
