using ProtonProfiles.Core.Diagnostics;

namespace ProtonProfiles.Core.Tests;

public class ConnectionLogTests
{
    private static (CdpNetworkParser Parser, List<ConnectionEntry> Out) NewParser()
    {
        var list = new List<ConnectionEntry>();
        return (new CdpNetworkParser(list.Add, "основное окно"), list);
    }

    private const string Request = """
        {"requestId":"1","loaderId":"L","documentURL":"https://mail.proton.me/u/0/inbox","type":"Fetch","timestamp":100.0,"wallTime":1790000000.5,
         "request":{"url":"https://mail.proton.me/api/core/v4/events/abc?ConversationCounts=1&token=SECRET#frag","method":"GET","headers":{}}}
        """;

    private const string Response = """
        {"requestId":"1","type":"Fetch","timestamp":100.1,
         "response":{"url":"https://mail.proton.me/api/core/v4/events/abc","status":200,"mimeType":"application/json","remoteIPAddress":"185.70.42.37",
           "remotePort":443,"protocol":"h2","fromDiskCache":false,"fromServiceWorker":true,
           "securityDetails":{"protocol":"TLS 1.3","cipher":"AES_128_GCM","subjectName":"proton.me","issuer":"R11","validTo":1800000000}}}
        """;

    [Fact]
    public void Completed_request_carries_remote_address_protocol_and_tls()
    {
        var (p, output) = NewParser();
        p.Handle("Network.requestWillBeSent", Request);
        p.Handle("Network.responseReceived", Response);
        Assert.Empty(output);
        p.Handle("Network.loadingFinished", """{"requestId":"1","timestamp":100.25,"encodedDataLength":2048}""");

        var e = Assert.Single(output);
        Assert.Equal(ConnectionOutcome.Completed, e.Outcome);
        Assert.Equal("mail.proton.me", e.Host);
        Assert.Equal(200, e.Status);
        Assert.Equal("185.70.42.37:443", e.RemoteEndpoint);
        Assert.Equal("h2", e.Protocol);
        Assert.Equal("TLS 1.3 AES_128_GCM", e.TlsText);
        Assert.Equal("R11", e.CertificateIssuer);
        Assert.Equal(2048, e.EncodedBytes);
        Assert.Equal(250, e.DurationMs!.Value, 3);
        Assert.Equal("Fetch", e.ResourceType);
        Assert.Contains("service worker", e.Source);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790000000500), e.StartedAt);
        Assert.Equal(0, p.PendingCount);
    }

    [Fact]
    public void Query_values_and_fragment_are_hidden()
    {
        var (p, output) = NewParser();
        p.Handle("Network.requestWillBeSent", Request);
        p.Handle("Network.loadingFinished", """{"requestId":"1","timestamp":101}""");
        var url = Assert.Single(output).Url;
        Assert.Equal("https://mail.proton.me/api/core/v4/events/abc?ConversationCounts=…&token=…#…", url);
        Assert.DoesNotContain("SECRET", url);
        Assert.Equal("https://x.example:8443/p", CdpNetworkParser.ShapeUrl("https://u:pw@x.example:8443/p"));
    }

    [Fact]
    public void Failed_cancelled_and_blocked_requests_are_classified()
    {
        var (p, output) = NewParser();
        foreach (var id in new[] { "a", "b", "c" })
            p.Handle("Network.requestWillBeSent", "{\"requestId\":\"" + id + "\",\"type\":\"XHR\",\"timestamp\":1,\"request\":{\"url\":\"https://" + id + ".example/\",\"method\":\"POST\"}}");
        p.Handle("Network.loadingFailed", """{"requestId":"a","timestamp":2,"errorText":"net::ERR_NAME_NOT_RESOLVED","canceled":false}""");
        p.Handle("Network.loadingFailed", """{"requestId":"b","timestamp":2,"errorText":"net::ERR_ABORTED","canceled":true}""");
        p.Handle("Network.loadingFailed", """{"requestId":"c","timestamp":2,"errorText":"","blockedReason":"mixed-content"}""");
        Assert.Equal([ConnectionOutcome.Failed, ConnectionOutcome.Cancelled, ConnectionOutcome.Blocked], output.Select(o => o.Outcome));
        Assert.Equal("net::ERR_NAME_NOT_RESOLVED", output[0].Error);
        Assert.Equal("blocked: mixed-content", output[2].Error);
        Assert.Equal("POST", output[0].Method);
        Assert.Equal("ошибка", output[0].StatusText);
    }

    [Fact]
    public void Redirect_emits_the_previous_hop()
    {
        var (p, output) = NewParser();
        p.Handle("Network.requestWillBeSent", """{"requestId":"r","type":"Document","timestamp":1,"request":{"url":"http://proton.me/","method":"GET"}}""");
        p.Handle("Network.requestWillBeSent", """{"requestId":"r","type":"Document","timestamp":1.1,"request":{"url":"https://proton.me/","method":"GET"},"redirectResponse":{"status":301,"remoteIPAddress":"185.70.42.45","remotePort":80,"protocol":"http/1.1"}}""");
        p.Handle("Network.loadingFinished", """{"requestId":"r","timestamp":1.2,"encodedDataLength":10}""");
        Assert.Equal(2, output.Count);
        Assert.Equal(ConnectionOutcome.Redirect, output[0].Outcome);
        Assert.Equal(301, output[0].Status);
        Assert.Equal("http://proton.me/", output[0].Url);
        Assert.Equal("185.70.42.45:80", output[0].RemoteEndpoint);
        Assert.Equal("https://proton.me/", output[1].Url);
    }

    [Fact]
    public void WebSocket_handshake_is_logged()
    {
        var (p, output) = NewParser();
        p.Handle("Network.webSocketCreated", """{"requestId":"w","url":"wss://mail.proton.me/ws?x=1"}""");
        p.Handle("Network.webSocketHandshakeResponseReceived", """{"requestId":"w","timestamp":5,"response":{"status":101}}""");
        var e = Assert.Single(output);
        Assert.Equal(ConnectionOutcome.WebSocket, e.Outcome);
        Assert.Equal(101, e.Status);
        Assert.Equal("wss://mail.proton.me/ws?x=…", e.Url);
    }

    [Fact]
    public void Non_network_schemes_malformed_json_and_unknown_ids_are_ignored()
    {
        var (p, output) = NewParser();
        p.Handle("Network.requestWillBeSent", """{"requestId":"d","timestamp":1,"request":{"url":"data:image/png;base64,AAAA","method":"GET"}}""");
        p.Handle("Network.requestWillBeSent", """{"requestId":"b","timestamp":1,"request":{"url":"blob:https://mail.proton.me/x","method":"GET"}}""");
        p.Handle("Network.loadingFinished", """{"requestId":"d","timestamp":2}""");
        p.Handle("Network.loadingFinished", """{"requestId":"zzz","timestamp":2}""");
        p.Handle("Network.requestWillBeSent", "{not json");
        p.Handle("Network.requestWillBeSent", "[]");
        p.Handle("Network.responseReceived", """{"requestId":"q"}""");
        Assert.Empty(output);
        Assert.Equal(0, p.PendingCount);
    }

    [Fact]
    public void IPv6_endpoint_is_bracketed()
    {
        var e = Entry("h") with { RemoteIp = "2a02:6ea0::1", RemotePort = 443 };
        Assert.Equal("[2a02:6ea0::1]:443", e.RemoteEndpoint);
    }

    [Fact]
    public void Pending_requests_are_bounded()
    {
        var (p, _) = NewParser();
        for (var i = 0; i < CdpNetworkParser.MaxPending + 500; i++)
            p.Handle("Network.requestWillBeSent", "{\"requestId\":\"" + i + "\",\"timestamp\":1,\"request\":{\"url\":\"https://x.example/" + i + "\",\"method\":\"GET\"}}");
        Assert.Equal(CdpNetworkParser.MaxPending, p.PendingCount);
    }

    private static ConnectionEntry Entry(string host, ConnectionOutcome outcome = ConnectionOutcome.Completed, string? ip = "1.2.3.4", long bytes = 10) =>
        new(DateTimeOffset.UtcNow, outcome, "GET", $"https://{host}/", host, "Fetch", 200, ip, 443, "h2", "TLS 1.3", "AES_128_GCM", "cn", "R11", null, null, bytes, 5, null, null);

    [Fact]
    public void Log_is_bounded_and_raises_events()
    {
        var log = new ConnectionLog(capacity: 3);
        var seen = 0;
        log.Added += _ => seen++;
        for (var i = 0; i < 5; i++) log.Add(Entry($"h{i}.example"));
        Assert.Equal(5, seen);
        Assert.Equal(5, log.TotalSeen);
        Assert.Equal(["h2.example", "h3.example", "h4.example"], log.Snapshot().Select(e => e.Host));
        var cleared = false;
        log.Cleared += () => cleared = true;
        log.Clear();
        Assert.True(cleared);
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Summary_groups_by_host()
    {
        var hosts = ConnectionLog.Summarize(
        [
            Entry("mail.proton.me", ip: "185.70.42.37", bytes: 100),
            Entry("mail.proton.me", ip: "185.70.42.38", bytes: 50),
            Entry("MAIL.proton.me", ConnectionOutcome.Failed, ip: null, bytes: 0),
            Entry("account.proton.me"),
        ]);
        Assert.Equal(2, hosts.Count);
        var mail = hosts[0];
        Assert.Equal(3, mail.Requests);
        Assert.Equal(1, mail.Failures);
        Assert.Equal("185.70.42.37, 185.70.42.38", mail.RemoteIps);
        Assert.Equal(150, mail.Bytes);
    }

    [Fact]
    public void Tsv_has_one_line_per_entry_without_tabs_in_cells()
    {
        var log = new ConnectionLog();
        log.Add(Entry("a.example") with { Error = "bad\tvalue\nnext" });
        var lines = log.ExportTsv().TrimEnd().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(ConnectionLog.TsvColumns.Length, lines[1].TrimEnd('\r').Split('\t').Length);
        Assert.Contains("\"RemoteIp\": \"1.2.3.4\"", log.ExportJson());
    }

    [Fact]
    public void Log_file_mirrors_entries_and_prunes_old_files()
    {
        using var env = new TestEnv();
        var id = Guid.NewGuid();
        var dir = env.Paths.ProfileLogDirectory(id);
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 12; i++) File.WriteAllText(Path.Combine(dir, $"connections-2026010{i / 10}-0000{i % 10:00}.tsv"), "old");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "keep");

        var log = new ConnectionLog();
        string path;
        using (var file = ConnectionLogFile.TryStart(env.Paths, id, log, DateTimeOffset.Now))
        {
            Assert.NotNull(file);
            path = file.FilePath;
            log.Add(Entry("mail.proton.me"));
        }
        log.Add(Entry("after-dispose.example")); // detached: not written

        var text = File.ReadAllLines(path);
        Assert.Equal(2, text.Length);
        Assert.Contains("mail.proton.me", text[1]);
        Assert.Equal(ConnectionLogFile.KeepFiles, Directory.GetFiles(dir, "connections-*.tsv").Length);
        Assert.True(File.Exists(Path.Combine(dir, "other.txt")));
        Assert.StartsWith(env.Paths.ProfileDirectory(id), path, StringComparison.Ordinal);
    }
}
