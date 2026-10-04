using System.Net;
using System.Net.Sockets;
using MaxMind.GeoIP2;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Privacy;

public sealed record GeoIpDatabaseInfo(string DatabaseType, DateTime BuildDate);
public sealed record GeoIpTimeZoneResolution(string TimeZoneId, GeoIpDatabaseInfo Database);

/// <summary>Offline City MMDB lookups. Addresses and credentials are never saved or exported.</summary>
public sealed class GeoIpTimeZoneDatabase(ManagedPaths paths)
{
    private const long MaximumBytes = 256 * 1024 * 1024;
    private static readonly object Gate = new();

    public GeoIpDatabaseInfo Inspect()
    {
        lock (Gate) { using var reader = Open(paths.GeoIpDatabasePath); return Info(reader); }
    }

    public GeoIpDatabaseInfo Install(string source)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(paths.GeoIpDirectory);
            var staging = Path.Combine(paths.GeoIpDirectory, Guid.NewGuid() + ".tmp");
            try
            {
                if (new FileInfo(source).Length is <= 0 or > MaximumBytes)
                    throw new InvalidDataException("Недопустимый размер MMDB-базы (максимум 256 МБ).");
                File.Copy(source, staging);
                GeoIpDatabaseInfo info;
                using (var reader = Open(staging)) info = Info(reader);
                File.Move(staging, paths.GeoIpDatabasePath, overwrite: true);
                return info;
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }

    public GeoIpTimeZoneResolution Resolve(IEnumerable<IPAddress> addresses)
    {
        var ips = addresses.Distinct().ToArray();
        if (ips.Length == 0 || ips.Length > 2 || ips.Any(ip => !IsPublic(ip)))
            throw new InvalidDataException("Не удалось получить публичный IP выхода через сеть профиля.");
        lock (Gate)
        {
            using var reader = Open(paths.GeoIpDatabasePath);
            var zones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ip in ips)
            {
                if (!reader.TryCity(ip, out var city) || city.Location.TimeZone is not { } zone
                    || BrowserTimeZone.Validate(zone) is not null)
                    throw new InvalidDataException("Локальная GeoIP-база не нашла часовой пояс IP выхода. Обновите базу или выберите пояс вручную.");
                zones.Add(zone);
            }
            if (zones.Count != 1)
                throw new InvalidDataException("IPv4 и IPv6 выхода относятся к разным часовым поясам. Проверьте прокси или выберите пояс вручную.");
            return new(zones.Single(), Info(reader));
        }
    }

    private static DatabaseReader Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Для режима «Авто по IP» установите GeoLite2 City или GeoIP2 City (.mmdb) в настройках профиля.");
        var reader = new DatabaseReader(path);
        if (reader.Metadata.DatabaseType is "GeoLite2-City" or "GeoIP2-City") return reader;
        reader.Dispose();
        throw new InvalidDataException("Нужна база GeoLite2 City или GeoIP2 City; Country/ASN не содержат часовой пояс.");
    }

    private static GeoIpDatabaseInfo Info(DatabaseReader reader) => new(reader.Metadata.DatabaseType, reader.Metadata.BuildDate);

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224 || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
                || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 198 && b[1] is 18 or 19
                || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2
                || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId != 0) return false;
        var v6 = ip.GetAddressBytes();
        return (v6[0] & 0xe0) == 0x20
            && !(v6[0] == 0x20 && v6[1] == 0x01 && (v6[2] < 2 || v6[2] == 0x0d && v6[3] == 0xb8))
            && !(v6[0] == 0x3f && v6[1] == 0xff && (v6[2] & 0xf0) == 0);
    }
}
