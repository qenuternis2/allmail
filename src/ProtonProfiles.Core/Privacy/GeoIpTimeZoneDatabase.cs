using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MaxMind.GeoIP2;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Privacy;

public sealed record GeoIpDatabaseInfo(string DatabaseType, DateTime BuildDate)
{
    public string TimeZoneSource { get; init; } = "MMDB record";
    public string BuildDateSource { get; init; } = "database";
}
public sealed record GeoIpTimeZoneResolution(string TimeZoneId, GeoIpDatabaseInfo Database);

/// <summary>Offline City MMDB or Legacy lookups. Addresses and credentials are never saved or exported.</summary>
public sealed class GeoIpTimeZoneDatabase(ManagedPaths paths)
{
    public const long MaximumBytes = 256 * 1024 * 1024;
    private static readonly object Gate = new();
    private sealed record ActiveDatabase(string Format, string Generation);
    private string ManifestPath => Path.Combine(paths.GeoIpDirectory, "active-city.json");

    public IReadOnlyList<string> InstalledFiles { get { lock (Gate) return Files(); } }

    private string[] Files()
    {
        if (!File.Exists(ManifestPath)) return [paths.GeoIpDatabasePath]; // Existing 0.1.32 installations.
        if (new FileInfo(ManifestPath).Length > 4096) throw new InvalidDataException("Повреждён выбор GeoIP-базы.");
        var active = JsonSerializer.Deserialize<ActiveDatabase>(File.ReadAllText(ManifestPath));
        if (active is null || !Guid.TryParseExact(active.Generation, "D", out _) || active.Format is not ("mmdb" or "legacy"))
            throw new InvalidDataException("Повреждён выбор GeoIP-базы.");
        var folder = Path.Combine(paths.GeoIpDirectory, "db-" + active.Generation);
        if (active.Format == "mmdb") return [Path.Combine(folder, "City.mmdb")];
        var files = new[] { Path.Combine(folder, "GeoIPCity.dat"), Path.Combine(folder, "GeoIPCityv6.dat") }.Where(File.Exists).ToArray();
        if (files.Length == 0) throw Missing();
        return files;
    }

    public GeoIpDatabaseInfo Inspect()
    {
        lock (Gate) return InspectFiles(Files());
    }

    private static GeoIpDatabaseInfo InspectFiles(string[] files)
    {
        if (files.Length == 1 && files[0].EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
        { using var reader = OpenMmdb(files[0]); return Info(reader); }
        var infos = new List<GeoIpDatabaseInfo>();
        foreach (var file in files) { using var reader = new LegacyGeoIpCityReader(file); infos.Add(reader.Info); }
        return infos[0] with { DatabaseType = "GeoIP Legacy City (" + string.Join(" + ", infos.Select(i => i.DatabaseType.EndsWith("IPv6", StringComparison.Ordinal) ? "IPv6" : "IPv4")) + ")", BuildDate = infos.Min(i => i.BuildDate) };
    }

    public GeoIpDatabaseInfo Install(params string[] sources)
        => InstallCore(sources, null, CancellationToken.None, out _);

    // The download may take minutes. A manual import or disabled updater must win over that download.
    internal (GeoIpDatabaseInfo Info, Guid Generation) InstallUpdate(string[] sources, Func<bool> mayCommit, CancellationToken cancellationToken)
    {
        var info = InstallCore(sources, mayCommit, cancellationToken, out var generation);
        return (info, generation);
    }

    private GeoIpDatabaseInfo InstallCore(string[] sources, Func<bool>? mayCommit, CancellationToken cancellationToken, out Guid installedGeneration)
    {
        installedGeneration = Guid.NewGuid();
        if (sources.Length is < 1 or > 2) throw new InvalidDataException("Выберите одну City MMDB или одну/две Legacy City базы (IPv4/IPv6).");
        lock (Gate)
        {
            var generation = installedGeneration.ToString("D");
            var folder = Path.Combine(paths.GeoIpDirectory, "db-" + generation);
            Directory.CreateDirectory(folder);
            var manifestTemp = Path.Combine(paths.GeoIpDirectory, generation + ".tmp");
            var committed = false;
            try
            {
                var format = "legacy";
                var installed = new List<string>();
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gzip = source.EndsWith(".dat.gz", StringComparison.OrdinalIgnoreCase);
                    var mmdb = source.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase);
                    if (!gzip && !mmdb && !source.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Поддерживаются City .mmdb, .dat и .dat.gz.");
                    if (mmdb && sources.Length != 1) throw new InvalidDataException("MMDB нельзя смешивать с Legacy базами.");
                    var staging = Path.Combine(folder, "input.tmp");
                    if (new FileInfo(source).Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Недопустимый размер GeoIP-базы (максимум 256 МБ).");
                    using (var input = File.OpenRead(source))
                    using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (gzip) { using var unzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true); CopyLimited(unzip, output); }
                        else CopyLimited(input, output);
                        output.Flush(flushToDisk: true);
                    }
                    File.SetLastWriteTimeUtc(staging, File.GetLastWriteTimeUtc(source));
                    string name;
                    if (mmdb) { using var reader = OpenMmdb(staging); format = "mmdb"; name = "City.mmdb"; }
                    else { using var reader = new LegacyGeoIpCityReader(staging); reader.ValidateTree(); name = reader.IsIPv6 ? "GeoIPCityv6.dat" : "GeoIPCity.dat"; }
                    var destination = Path.Combine(folder, name);
                    if (File.Exists(destination)) throw new InvalidDataException("Выбраны две базы для одной версии IP. Нужны IPv4 и IPv6.");
                    File.Move(staging, destination);
                    installed.Add(destination);
                }
                var info = InspectFiles(installed.Order(StringComparer.Ordinal).ToArray());
                cancellationToken.ThrowIfCancellationRequested();
                if (mayCommit is not null && !mayCommit()) throw new OperationCanceledException("Загрузка отменена: настройки или активная база изменились.");
                File.WriteAllText(manifestTemp, JsonSerializer.Serialize(new ActiveDatabase(format, generation)));
                File.Move(manifestTemp, ManifestPath, overwrite: true); // Whole generation switches atomically, including the IPv4/IPv6 pair.
                committed = true;
                return info;
            }
            finally
            {
                if (File.Exists(manifestTemp)) File.Delete(manifestTemp);
                if (!committed && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
    }

    // Only updater-owned, obsolete generations are eligible. Manual imports and unknown files stay untouched.
    internal void RemoveObsoleteUpdate(Guid generation)
    {
        lock (Gate)
        {
            var folder = Path.Combine(paths.GeoIpDirectory, "db-" + generation.ToString("D"));
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0
                || Files().Any(file => string.Equals(Path.GetDirectoryName(file), folder, StringComparison.OrdinalIgnoreCase))) return;
            var allowed = new[] { "GeoIPCity.dat", "GeoIPCityv6.dat" };
            if (Directory.EnumerateFileSystemEntries(folder).Any(file => !allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal)
                || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)) return;
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void CopyLimited(Stream input, Stream output)
    {
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = input.Read(buffer)) != 0)
        { total += count; if (total > MaximumBytes) throw new InvalidDataException("Распакованная GeoIP-база превышает 256 МБ."); output.Write(buffer, 0, count); }
    }

    public GeoIpTimeZoneResolution Resolve(IEnumerable<IPAddress> addresses)
    {
        var ips = addresses.Select(ip => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).Distinct().ToArray();
        if (ips.Length == 0 || ips.Length > 2 || ips.Any(ip => !IsPublic(ip)))
            throw new InvalidDataException("Не удалось получить публичный IP выхода через сеть профиля.");
        lock (Gate)
        {
            var files = Files();
            var zones = new HashSet<string>(StringComparer.Ordinal);
            using var mmdb = files.Length == 1 && files[0].EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase) ? OpenMmdb(files[0]) : null;
            var legacy = new List<LegacyGeoIpCityReader>();
            try
            {
                if (mmdb is null) foreach (var file in files) legacy.Add(new(file));
                foreach (var ip in ips)
                {
                    string? zone;
                    if (mmdb is not null) zone = mmdb.TryCity(ip, out var city) ? city.Location.TimeZone : null;
                    else
                    {
                        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
                        var reader = legacy.FirstOrDefault(r => r.IsIPv6 == v6) ?? (!v6 ? legacy.FirstOrDefault(r => r.IsIPv6) : null);
                        if (reader is null) throw new InvalidDataException("Для IPv6 выхода установите GeoIPCityv6.dat.gz вместе с IPv4-базой. IPv6 нельзя игнорировать в режиме Авто.");
                        zone = reader.FindTimeZone(ip);
                    }
                    if (zone is null || BrowserTimeZone.Validate(zone) is not null)
                        throw new InvalidDataException("Локальная GeoIP-база не нашла часовой пояс IP выхода. Обновите базу или выберите пояс вручную.");
                    zones.Add(zone);
                }
                if (zones.Count != 1) throw new InvalidDataException("IPv4 и IPv6 выхода относятся к разным часовым поясам. Проверьте прокси или выберите пояс вручную.");
                return new(zones.Single(), mmdb is not null ? Info(mmdb) : InspectFiles(files));
            }
            finally { foreach (var reader in legacy) reader.Dispose(); }
        }
    }

    private static FileNotFoundException Missing() => new("Для режима «Авто по IP» установите GeoIPCity.dat.gz / GeoIPCityv6.dat.gz (Mailfud) или City MMDB в настройках профиля.");
    private static DatabaseReader OpenMmdb(string path)
    {
        if (!File.Exists(path)) throw Missing();
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
