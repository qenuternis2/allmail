using System.Text.Json;

namespace ProtonProfiles.Core.Diagnostics;

/// <summary>
/// Turns DevTools Protocol Network-domain events (JSON strings, as delivered by WebView2's DevToolsProtocolEventReceived)
/// into <see cref="ConnectionEntry"/> records. Requests are correlated by requestId; an entry is emitted when a request
/// finishes, fails, redirects or a WebSocket handshake completes. Not thread-safe: feed it from one thread.
/// </summary>
public sealed class CdpNetworkParser
{
    public const int MaxPending = 2000;

    public static readonly string[] Events =
    [
        "Network.requestWillBeSent",
        "Network.responseReceived",
        "Network.loadingFinished",
        "Network.loadingFailed",
        "Network.webSocketCreated",
        "Network.webSocketHandshakeResponseReceived",
    ];

    private sealed class Pending
    {
        public required string Url;
        public required string Method;
        public required string Type;
        public required DateTimeOffset Started;
        public double StartTimestamp;
        public JsonElement? Response;
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Action<ConnectionEntry> _emit;
    private readonly string? _source;

    /// <param name="source">Label of the view the events come from, e.g. "основное окно".</param>
    public CdpNetworkParser(Action<ConnectionEntry> emit, string? source = null)
    {
        _emit = emit;
        _source = source;
    }

    public int PendingCount => _pending.Count;

    /// <summary>Handles one event; malformed or irrelevant JSON is ignored.</summary>
    public void Handle(string eventName, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var p = doc.RootElement;
            if (p.ValueKind != JsonValueKind.Object || Str(p, "requestId") is not { } id) return;
            switch (eventName)
            {
                case "Network.requestWillBeSent": OnRequest(id, p); break;
                case "Network.responseReceived": OnResponse(id, p); break;
                case "Network.loadingFinished": OnFinished(id, p); break;
                case "Network.loadingFailed": OnFailed(id, p); break;
                case "Network.webSocketCreated": OnWebSocketCreated(id, p); break;
                case "Network.webSocketHandshakeResponseReceived": OnWebSocketHandshake(id, p); break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private void OnRequest(string id, JsonElement p)
    {
        if (!p.TryGetProperty("request", out var req)) return;
        var url = Str(req, "url");
        if (url is null || !IsNetworkUrl(url)) return;
        if (_pending.TryGetValue(id, out var previous) && p.TryGetProperty("redirectResponse", out var redirect))
        {
            // Same requestId continues after a redirect: emit the hop that just ended.
            previous.Response = redirect.Clone();
            Emit(previous, ConnectionOutcome.Redirect, Num(p, "timestamp"), null, null);
            _pending.Remove(id);
        }
        Track(id, new Pending
        {
            Url = url,
            Method = Str(req, "method") ?? "GET",
            Type = Str(p, "type") ?? string.Empty,
            Started = Num(p, "wallTime") is { } wall ? DateTimeOffset.FromUnixTimeMilliseconds((long)(wall * 1000)) : DateTimeOffset.UtcNow,
            StartTimestamp = Num(p, "timestamp") ?? 0,
        });
    }

    private void OnResponse(string id, JsonElement p)
    {
        if (!_pending.TryGetValue(id, out var pending) || !p.TryGetProperty("response", out var resp)) return;
        pending.Response = resp.Clone();
        if (Str(p, "type") is { Length: > 0 } t) pending.Type = t;
    }

    private void OnFinished(string id, JsonElement p)
    {
        if (!_pending.Remove(id, out var pending)) return;
        Emit(pending, ConnectionOutcome.Completed, Num(p, "timestamp"), (long?)Num(p, "encodedDataLength"), null);
    }

    private void OnFailed(string id, JsonElement p)
    {
        if (!_pending.Remove(id, out var pending)) return;
        var canceled = p.TryGetProperty("canceled", out var c) && c.ValueKind == JsonValueKind.True;
        var blocked = Str(p, "blockedReason");
        var error = Str(p, "errorText");
        if (blocked is not null) error = string.IsNullOrEmpty(error) ? $"blocked: {blocked}" : $"{error} (blocked: {blocked})";
        var outcome = blocked is not null ? ConnectionOutcome.Blocked : canceled ? ConnectionOutcome.Cancelled : ConnectionOutcome.Failed;
        Emit(pending, outcome, Num(p, "timestamp"), null, error);
    }

    private void OnWebSocketCreated(string id, JsonElement p)
    {
        if (Str(p, "url") is not { } url) return;
        Track(id, new Pending { Url = url, Method = "GET", Type = "WebSocket", Started = DateTimeOffset.UtcNow, StartTimestamp = 0 });
    }

    private void OnWebSocketHandshake(string id, JsonElement p)
    {
        if (!_pending.Remove(id, out var pending)) return;
        if (p.TryGetProperty("response", out var resp)) pending.Response = resp.Clone();
        Emit(pending, ConnectionOutcome.WebSocket, null, null, null);
    }

    private void Track(string id, Pending pending)
    {
        _pending[id] = pending;
        _order.Enqueue(id);
        // Long-lived or never-finished requests must not grow memory without bound.
        while (_pending.Count > MaxPending && _order.TryDequeue(out var old)) _pending.Remove(old);
        if (_order.Count > MaxPending * 4)
        {
            var live = _order.Where(_pending.ContainsKey).ToList();
            _order.Clear();
            foreach (var k in live) _order.Enqueue(k);
        }
    }

    private void Emit(Pending r, ConnectionOutcome outcome, double? endTimestamp, long? encodedBytes, string? error)
    {
        JsonElement? resp = r.Response;
        JsonElement sec = default;
        var hasSec = resp is { } rr && rr.TryGetProperty("securityDetails", out sec) && sec.ValueKind == JsonValueKind.Object;
        var sources = new List<string>();
        if (_source is not null) sources.Add(_source);
        if (resp is { } r1)
        {
            if (Bool(r1, "fromServiceWorker")) sources.Add("service worker");
            if (Bool(r1, "fromDiskCache")) sources.Add("дисковый кэш");
            if (Bool(r1, "fromPrefetchCache")) sources.Add("prefetch");
        }
        var status = resp is { } r2 && Num(r2, "status") is { } st ? (int?)st : null;
        double? duration = endTimestamp is { } end && r.StartTimestamp > 0 && end >= r.StartTimestamp ? (end - r.StartTimestamp) * 1000 : null;

        _emit(new ConnectionEntry(
            StartedAt: r.Started,
            Outcome: outcome,
            Method: r.Method,
            Url: ShapeUrl(r.Url),
            Host: HostOf(r.Url),
            ResourceType: r.Type,
            Status: status,
            RemoteIp: resp is { } r3 ? NullIfEmpty(Str(r3, "remoteIPAddress")) : null,
            RemotePort: resp is { } r4 && Num(r4, "remotePort") is { } port && port > 0 ? (int)port : null,
            Protocol: resp is { } r5 ? NullIfEmpty(Str(r5, "protocol")) : null,
            TlsVersion: hasSec ? Str(sec, "protocol") : null,
            Cipher: hasSec ? Str(sec, "cipher") : null,
            CertificateSubject: hasSec ? Str(sec, "subjectName") : null,
            CertificateIssuer: hasSec ? Str(sec, "issuer") : null,
            CertificateValidTo: hasSec && Num(sec, "validTo") is { } vt ? DateTimeOffset.FromUnixTimeSeconds((long)vt) : null,
            MimeType: resp is { } r6 ? NullIfEmpty(Str(r6, "mimeType")) : null,
            EncodedBytes: encodedBytes,
            DurationMs: duration,
            Source: sources.Count == 0 ? null : string.Join(", ", sources),
            Error: error));
    }

    /// <summary>Only real network schemes are logged; data:, blob:, chrome-extension: etc. never touch the network.</summary>
    public static bool IsNetworkUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps scheme, host, port and path; query values and the fragment are replaced because they can carry one-time
    /// tokens. Userinfo is dropped.
    /// </summary>
    public static string ShapeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "[url]";
        var port = u.IsDefaultPort ? string.Empty : $":{u.Port}";
        var query = string.IsNullOrEmpty(u.Query) ? string.Empty : "?" + string.Join("&",
            u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=', 2)[0] + "=…"));
        var fragment = string.IsNullOrEmpty(u.Fragment) ? string.Empty : "#…";
        return $"{u.Scheme}://{u.Host}{port}{u.AbsolutePath}{query}{fragment}";
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : string.Empty;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
