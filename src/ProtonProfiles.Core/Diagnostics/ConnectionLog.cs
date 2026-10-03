using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProtonProfiles.Core.Diagnostics;

public enum ConnectionOutcome { Completed, Failed, Cancelled, Blocked, WebSocket, Redirect }

/// <summary>One finished network request of a profile as seen by the browser engine (DevTools Network domain).</summary>
public sealed record ConnectionEntry(
    DateTimeOffset StartedAt,
    ConnectionOutcome Outcome,
    string Method,
    string Url,
    string Host,
    string ResourceType,
    int? Status,
    string? RemoteIp,
    int? RemotePort,
    string? Protocol,
    string? TlsVersion,
    string? Cipher,
    string? CertificateSubject,
    string? CertificateIssuer,
    DateTimeOffset? CertificateValidTo,
    string? MimeType,
    long? EncodedBytes,
    double? DurationMs,
    string? Source,
    string? Error)
{
    public string RemoteEndpoint => RemoteIp is null ? string.Empty
        : RemotePort is null ? RemoteIp
        : RemoteIp.Contains(':') ? $"[{RemoteIp}]:{RemotePort}" : $"{RemoteIp}:{RemotePort}";

    public string StatusText => Outcome switch
    {
        ConnectionOutcome.Failed => "ошибка",
        ConnectionOutcome.Cancelled => "отменён",
        ConnectionOutcome.Blocked => "заблокирован",
        _ => Status?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
    };

    public string TlsText => TlsVersion is null ? string.Empty : Cipher is null ? TlsVersion : $"{TlsVersion} {Cipher}";

    public string SizeText => EncodedBytes is null ? string.Empty
        : EncodedBytes < 1024 ? $"{EncodedBytes} Б"
        : EncodedBytes < 1024 * 1024 ? $"{EncodedBytes / 1024.0:0.#} КБ"
        : $"{EncodedBytes / (1024.0 * 1024):0.##} МБ";

    public string DurationText => DurationMs is null ? string.Empty : $"{DurationMs:0} мс";

    public string TimeText => StartedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Details => string.Join("; ", new[]
    {
        Source, Error, MimeType,
        CertificateSubject is null ? null : $"сертификат: {CertificateSubject}, издатель {CertificateIssuer}" +
            (CertificateValidTo is { } v ? $", до {v:yyyy-MM-dd}" : string.Empty),
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>Per-host aggregate for the "hosts" view.</summary>
public sealed record HostSummary(string Host, int Requests, int Failures, string RemoteIps, string Protocols, string Tls, string Issuer, long Bytes, DateTimeOffset LastSeen);

/// <summary>
/// Bounded in-memory log of a profile's connections, kept for the lifetime of one browser generation. Entries are local
/// diagnostics for the user only and are never part of the shareable diagnostics report (spec §7).
/// </summary>
public sealed class ConnectionLog
{
    public const int DefaultCapacity = 5000;
    private readonly object _gate = new();
    private readonly LinkedList<ConnectionEntry> _entries = new();

    public ConnectionLog(int capacity = DefaultCapacity) => Capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Capacity { get; }
    public long TotalSeen { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>Raised on the thread that added the entry.</summary>
    public event Action<ConnectionEntry>? Added;
    public event Action? Cleared;

    public void Add(ConnectionEntry entry)
    {
        lock (_gate)
        {
            _entries.AddLast(entry);
            TotalSeen++;
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
        Added?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Cleared?.Invoke();
    }

    public IReadOnlyList<ConnectionEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public static IReadOnlyList<HostSummary> Summarize(IEnumerable<ConnectionEntry> entries) =>
        entries.GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HostSummary(
                g.Key,
                g.Count(),
                g.Count(e => e.Outcome is ConnectionOutcome.Failed or ConnectionOutcome.Blocked),
                Join(g.Select(e => e.RemoteIp)),
                Join(g.Select(e => e.Protocol)),
                Join(g.Select(e => e.TlsVersion)),
                Join(g.Select(e => e.CertificateIssuer)),
                g.Sum(e => e.EncodedBytes ?? 0),
                g.Max(e => e.StartedAt)))
            .OrderByDescending(h => h.Requests)
            .ToList();

    private static string Join(IEnumerable<string?> values) =>
        string.Join(", ", values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase));

    public static readonly string[] TsvColumns =
        ["time", "outcome", "method", "status", "type", "url", "remote", "protocol", "tls", "cipher", "cert_issuer", "bytes", "ms", "source", "error"];

    public static string TsvHeader => string.Join('\t', TsvColumns);

    public static string ToTsv(ConnectionEntry e) => string.Join('\t', new[]
    {
        e.StartedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        e.Outcome.ToString(), e.Method, e.Status?.ToString(CultureInfo.InvariantCulture), e.ResourceType, e.Url, e.RemoteEndpoint,
        e.Protocol, e.TlsVersion, e.Cipher, e.CertificateIssuer,
        e.EncodedBytes?.ToString(CultureInfo.InvariantCulture), e.DurationMs?.ToString("0", CultureInfo.InvariantCulture),
        e.Source, e.Error,
    }.Select(Cell));

    private static string Cell(string? v) => string.IsNullOrEmpty(v) ? string.Empty : v.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public string ExportTsv()
    {
        var sb = new StringBuilder().AppendLine(TsvHeader);
        foreach (var e in Snapshot()) sb.AppendLine(ToTsv(e));
        return sb.ToString();
    }

    public string ExportJson() => JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
