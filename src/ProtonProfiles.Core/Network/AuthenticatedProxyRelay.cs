using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Network;

/// <summary>Per-profile HTTP forwarder. CONNECT stays opaque; only the configured upstream is connected.</summary>
public sealed class AuthenticatedProxyRelay : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ProxyEndpoint _upstream;
    private readonly string _authorization;
    private readonly Action<string> _problem;
    private readonly Task _accept;
    private readonly CancellationToken _token;
    public ProxyEndpoint Endpoint { get; }

    public AuthenticatedProxyRelay(ProxyEndpoint upstream, ProxyCredential credential, Action<string> problem)
    {
        _upstream = upstream;
        _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.UserName + ":" + credential.Password));
        _problem = problem;
        _token = _stop.Token;
        _listener.Start();
        ProxyEndpoint.TryCreate("http", "127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port, out var endpoint, out _);
        Endpoint = endpoint!;
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_token);
                _clients.TryAdd(client, 0);
                _ = HandleAsync(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient downstream)
    {
        using (downstream)
        try
        {
            var stream = downstream.GetStream();
            var headers = await ReadHeadersAsync(stream, _token).WaitAsync(TimeSpan.FromSeconds(15), _token);
            var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            var first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] != "HTTP/1.1" || !first[0].All(char.IsAsciiLetter))
                throw new InvalidDataException();
            var tunnel = first[0] == "CONNECT";
            if (tunnel)
            {
                if (!ProxyEndpoint.TryParse("http://" + first[1], out _, out _)) throw new InvalidDataException();
            }
            else if (!Uri.TryCreate(first[1], UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo.Length != 0)
                throw new InvalidDataException();
            // Proxy secrets are added only to the outer upstream exchange. Browser-supplied
            // proxy credentials and connection reuse are never forwarded to another target.
            var outbound = tunnel
                ? first[0] + " " + first[1] + " HTTP/1.1\r\nHost: " + first[1] + "\r\n"
                : string.Join("\r\n", lines.Where((line, index) => index == 0 || !IsHeader(line, "Proxy-Authorization")
                    && !IsHeader(line, "Proxy-Connection") && !IsHeader(line, "Connection"))) + "\r\nConnection: close\r\nProxy-Connection: close\r\n";
            outbound += "Proxy-Authorization: " + _authorization + "\r\n\r\n";
            // Only CONNECT can be retried without replaying an HTTP request body.
            for (var attempt = 0; attempt < (tunnel ? 2 : 1); attempt++)
            {
                using var upstream = new TcpClient();
                _clients.TryAdd(upstream, 0);
                try
                {
                    await upstream.ConnectAsync(_upstream.Host, _upstream.Port, _token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), _token);
                    var network = upstream.GetStream();
                    await network.WriteAsync(Encoding.Latin1.GetBytes(outbound), _token);
                    Task? upload = tunnel ? null : stream.CopyToAsync(network, _token);
                    var response = await ReadHeadersAsync(network, _token).WaitAsync(TimeSpan.FromSeconds(15), _token);
                    var statusLine = response.Split("\r\n")[0].Split(' ');
                    if (statusLine.Length < 2 || !int.TryParse(statusLine[1], out var status)) throw new IOException();
                    if (status == 407)
                    {
                        if (tunnel && attempt == 0) continue;
                        _problem("Прокси отклонил учётные данные. Проверьте логин и пароль в настройках профиля.");
                        await ErrorAsync(stream, "502 Bad Gateway", _token);
                        return;
                    }
                    if (tunnel)
                    {
                        if (status != 200) { await ErrorAsync(stream, "502 Bad Gateway", _token); return; }
                        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _token);
                        upload = stream.CopyToAsync(network, _token);
                    }
                    else
                    {
                        // Never expose an upstream proxy challenge to the browser; prevent
                        // pooling unauthenticated follow-up HTTP requests on this connection.
                        var responseLines = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                        var forwarded = string.Join("\r\n", responseLines.Where((line, index) => index == 0
                            || !IsHeader(line, "Proxy-Authenticate") && !IsHeader(line, "Connection"))) + "\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.Latin1.GetBytes(forwarded), _token);
                    }
                    await Task.WhenAny(upload!, network.CopyToAsync(stream, _token));
                    return;
                }
                finally { _clients.TryRemove(upstream, out _); }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException or TimeoutException)
        {
            if (!_token.IsCancellationRequested)
                try { await ErrorAsync(downstream.GetStream(), "502 Bad Gateway", _token); } catch { }
        }
        finally { _clients.TryRemove(downstream, out _); }
    }

    private static bool IsHeader(string line, string name) => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase);
    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var one = new byte[1];
        var tail = 0u;
        while (bytes.Length < 65536 && await stream.ReadAsync(one, token) != 0)
        {
            bytes.WriteByte(one[0]);
            tail = (tail << 8) | one[0];
            if (tail == 0x0d0a0d0a) return Encoding.Latin1.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Incomplete proxy headers.");
    }
    private static Task ErrorAsync(Stream stream, string status, CancellationToken token) => stream.WriteAsync(
        Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token).AsTask();

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        // Cancellation closes the listener synchronously; the asynchronous accept
        // loop owns no UI thread work and can finish after controller disposal.
        _ = _accept;
    }
}
