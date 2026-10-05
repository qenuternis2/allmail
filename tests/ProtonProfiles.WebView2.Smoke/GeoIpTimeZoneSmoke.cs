using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProtonProfiles.App.Browser;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

internal static class GeoIpTimeZoneSmoke
{
    public static async Task RunAsync(Window window, string root, bool legacy = false)
    {
        var database = new GeoIpTimeZoneDatabase(new ManagedPaths(Path.Combine(root, legacy ? "legacy-geoip" : "geoip")));
        if (legacy) database.Install(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-city", "GeoIPCity.dat.gz"), Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-city", "GeoIPCityv6.dat.gz"));
        else database.Install(Path.Combine(AppContext.BaseDirectory, "fixtures", "GeoIP2-City-Test.mmdb"));
        foreach (var policy in new[] { GraphicsPolicy.RuntimeDefault, GraphicsPolicy.StrictFingerprintExperimental })
        {
            using var proxy = new IpServer(true);
            using var direct = new IpServer(false);
            if (!ProxyEndpoint.TryParse($"http://127.0.0.1:{proxy.Port}", out var endpoint, out var error)) throw new InvalidOperationException(error);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, (legacy ? "legacy-geoip-" : "geoip-") + policy), new()
            { AdditionalBrowserArguments = BrowserArguments.Build(endpoint, graphics: policy) + " --site-per-process", ExclusiveUserDataFolderAccess = true });
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
            try
            {
                using var view = new WebView2(); window.Content = view;
                await view.EnsureCoreWebView2Async(environment);
                var core = view.CoreWebView2;
                var auth = 0;
                core.BasicAuthenticationRequested += (_, e) => {
                    if (ProxyChallengeMatcher.Evaluate(endpoint, e.Uri, isProxyChallenge: true) != ProxyChallengeMatcher.Decision.ReleaseProxyCredentials) { e.Cancel = true; return; }
                    auth++; e.Response.UserName = "fixture"; e.Response.Password = "fixture-secret";
                };
                await ClientHintsRequests.ForCore(core, () => true, null, stripClientHints: UserAgentHintsPrivacy.IsEnabled(policy)).ConfigureAsync();
                var v4 = $"http://geoip-egress.invalid:{direct.Port}/v4";
                var v6 = $"http://geoip-egress.invalid:{direct.Port}/unavailable-v6";
                var addresses = await AutoTimeZoneBootstrap.DiscoverAsync(core, ipv4Url: v4, ipv6Url: v6);
                var resolution = database.Resolve(addresses);
                if (addresses.Length != 1 || resolution.TimeZoneId != "Europe/London" || auth == 0 || direct.Requests != 0)
                    throw new InvalidOperationException("Auto GeoIP proxy/auth/IPv6 absence mismatch.");
                foreach (var headers in proxy.Headers.Where(h => h.Contains("geoip-egress.invalid")))
                {
                    if (headers.Contains("Referer:", StringComparison.OrdinalIgnoreCase) || headers.Contains("protonprofiles.invalid", StringComparison.OrdinalIgnoreCase)
                        || !headers.Contains("Origin: null", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("IP discovery sent identifying origin/referrer: " + headers);
                    if (UserAgentHintsPrivacy.IsEnabled(policy) && headers.Contains("sec-ch-ua", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("IP discovery leaked native Client Hints.");
                }
                var dual = await AutoTimeZoneBootstrap.DiscoverAsync(core, ipv4Url: v4, ipv6Url: $"http://geoip-egress.invalid:{direct.Port}/v6");
                if (dual.Length != 2) throw new InvalidOperationException("IPv6 egress was not observed.");
                try { database.Resolve(dual); throw new InvalidOperationException("Conflicting IPv4/IPv6 zones accepted."); }
                catch (InvalidDataException) { }
                // Resolution is frozen before the real privacy bootstrap, which receives the effective zone exactly once.
                string? failure = null;
                var config = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "geoip", GraphicsPolicy = policy, BrowserTimeZoneId = resolution.TimeZoneId };
                await UserAgentHintsBootstrap.ApplyAsync(core, config, onFailure: reason => { failure = reason; return Task.CompletedTask; });
                if (await core.ExecuteScriptAsync(BrowserTimeZone.VerificationScript(resolution.TimeZoneId)) != "true" || failure is not null)
                    throw new InvalidOperationException("Resolved GeoIP zone not applied natively: " + failure);
                foreach (var host in new[] { "allmail-smoke.test", "allmail-frame.test" })
                    core.SetVirtualHostNameToFolderMapping(host, AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
                using (var report = JsonDocument.Parse(await TimeZoneSmoke.ObserveAsync(core)))
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById(resolution.TimeZoneId);
                    foreach (var scope in new[] { "main", "same", "cross", "worker" }) {
                        var value = report.RootElement.GetProperty(scope);
                        if (value.GetProperty("timeZone").GetString() != resolution.TimeZoneId || !value.GetProperty("nativeDate").GetBoolean() || !value.GetProperty("nativeIntl").GetBoolean()
                            || value.GetProperty("winter").GetInt32() != -(int)tz.GetUtcOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)).TotalMinutes
                            || value.GetProperty("summer").GetInt32() != -(int)tz.GetUtcOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)).TotalMinutes)
                            throw new InvalidOperationException("GeoIP first-script timezone mismatch: " + scope + " " + value);
                    }
                    Console.WriteLine((legacy ? "GeoIP Legacy startup " : "GeoIP startup ") + policy + "; main/same/cross/worker first script: " + report.RootElement);
                }
                await TimeZoneSmoke.NavigateBlankAsync(core);
                // No destination navigation is permitted if discovery fails. Never retry using a direct HttpClient.
                proxy.Stop();
                try { await AutoTimeZoneBootstrap.DiscoverAsync(core, ipv4Url: v4, ipv6Url: v6); throw new InvalidOperationException("Stopped proxy discovery succeeded."); }
                catch (InvalidDataException) { }
                if (direct.Requests != 0 || core.Source != "about:blank") throw new InvalidOperationException("Discovery fell back to a direct destination.");
                Console.WriteLine((legacy ? "PASS: Legacy GeoIP auto timezone " : "PASS: GeoIP auto timezone ") + policy + (legacy ? "; real Legacy DAT/GZIP reader and offline GeoTimeZone Europe/London;" : "; real City MMDB offline Europe/London;") + " same-controller proxy Basic auth; IPv4/IPv6 and unavailable IPv6; conflicting zones blocked; Origin null, no Referer/internal origin; native winter/summer timezone; stopped proxy blocks startup, direct receivers zero.");
            }
            finally { window.Content = null; await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        }
    }

    internal sealed class IpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly Task _loop;
        private readonly bool _proxy;
        private readonly X509Certificate2? _certificate;
        private int _requests;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Requests => Volatile.Read(ref _requests);
        public ConcurrentBag<string> Headers { get; } = [];
        public ConcurrentDictionary<string,int> AuthChallenges { get; } = new();
        public ConcurrentDictionary<string,int> AuthAccepted { get; } = new();
        public IpServer(bool proxy, bool https = false)
        {
            if (https)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=api.ipify.org", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("api.ipify.org"); san.AddDnsName("api6.ipify.org");
                request.CertificateExtensions.Add(san.Build());
                using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
            }
            _proxy = proxy; _listener.Start();
            _loop = Task.Run(async () => {
                try {
                    while (!_stop.IsCancellationRequested) {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(client);
                        _ = Task.Run(async () => { using (client) try { await HandleAsync(client); } catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or System.Security.Authentication.AuthenticationException) { } });
                    }
                } catch (OperationCanceledException) { } catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }
        private async Task HandleAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var headers = await ReadHeadersAsync(stream); if (headers.Length == 0) return;
            Interlocked.Increment(ref _requests); Headers.Add(headers);
            var first = headers.Split("\r\n")[0];
            var target = first.Split(' ')[1];
            var authFixture = first.StartsWith("CONNECT ",StringComparison.Ordinal) && target.EndsWith(".allmail-auth.test:443",StringComparison.Ordinal);
            var firstChallenge = authFixture && AuthChallenges.TryAdd(target,0);
            if (_proxy && (firstChallenge || target.StartsWith("reject.allmail-auth.test:",StringComparison.Ordinal)
                || !headers.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("fixture:fixture-secret")), StringComparison.OrdinalIgnoreCase))) {
                AuthChallenges.AddOrUpdate(target,1,(_,count)=>count+1);
                await RespondAsync(stream, "407 Proxy Authentication Required", "", "Proxy-Authenticate: Basic realm=\""+(authFixture?target:"fixture")+"\"\r\n"); return;
            }
            if (authFixture) AuthAccepted.AddOrUpdate(target,1,(_,count)=>count+1);
            if (_certificate is not null && first.StartsWith("CONNECT ", StringComparison.Ordinal))
            {
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
                using var tls = new SslStream(stream, false);
                await tls.AuthenticateAsServerAsync(_certificate);
                var inner = await ReadHeadersAsync(tls); if (inner.Length == 0) return;
                Headers.Add(inner);
                if (authFixture)
                {
                    if (inner.Contains("Authorization: Basic ",StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Fixture received proxy credentials inside a website TLS request.");
                    if (target.StartsWith("server401.",StringComparison.Ordinal))
                        await RespondAsync(tls,"401 Unauthorized","", "WWW-Authenticate: Basic realm=\"website\"\r\n");
                    else if (target.StartsWith("document.",StringComparison.Ordinal))
                        await RespondAsync(tls,"200 OK","<!doctype html><title>First network page</title><script>globalThis.first={href:location.href,crypto:!!crypto.subtle,rtc:typeof RTCPeerConnection==='undefined',zone:Intl.DateTimeFormat().resolvedOptions().timeZone};</script>",contentType:"text/html");
                    else await RespondAsync(tls,"200 OK","{\"ok\":true}");
                    return;
                }
                await RespondAsync(tls, first.Contains("api6.ipify.org:", StringComparison.Ordinal) ? "503 Unavailable" : "200 OK",
                    first.Contains("api6.ipify.org:", StringComparison.Ordinal) ? "" : "{\"ip\":\"81.2.69.160\"}");
                return;
            }
            if (first.Contains("/unavailable-v6")) { await RespondAsync(stream, "503 Unavailable", ""); return; }
            var ip = first.Contains("/v6") ? "2001:218::" : "81.2.69.160";
            await RespondAsync(stream, "200 OK", JsonSerializer.Serialize(new { ip }));
        }
        private static async Task<string> ReadHeadersAsync(Stream stream)
        {
            var bytes = new List<byte>(); var one = new byte[1];
            while (bytes.Count < 32768 && await stream.ReadAsync(one) > 0) {
                bytes.Add(one[0]); if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }
        private static Task RespondAsync(Stream stream, string status, string body, string extra = "", string contentType="application/json") =>
            stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extra}\r\n{body}")).AsTask();
        public void Stop() { if (_stop.IsCancellationRequested) return; _stop.Cancel(); _listener.Stop(); foreach (var client in _clients) client.Dispose(); }
        public void Dispose() { Stop(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); _certificate?.Dispose(); }
    }
}
