using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GeoTimeZone;

namespace ProtonProfiles.Core.Privacy;

/// <summary>Managed reader for MaxMind Legacy City radix trees (editions 2/6/30/31).
/// File offsets follow the documented GeoIPCity.c format; no native library or network lookup.</summary>
public sealed class LegacyGeoIpCityReader : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _segments;
    private readonly long _dataEnd;
    public bool IsIPv6 { get; }
    public GeoIpDatabaseInfo Info { get; }

    public LegacyGeoIpCityReader(string path)
    {
        var length = new FileInfo(path).Length;
        if (length < 14 || length > GeoIpTimeZoneDatabase.MaximumBytes) throw Bad();
        var footer = new byte[(int)Math.Min(length, 1024)];
        using (var stream = File.OpenRead(path)) { stream.Seek(-footer.Length, SeekOrigin.End); stream.ReadExactly(footer); }
        var marker = -1;
        // The format's structure marker must be in its last 20 bytes.
        for (var i = footer.Length - 7; i >= Math.Max(0, footer.Length - 20); i--)
            if (footer[i] == 255 && footer[i + 1] == 255 && footer[i + 2] == 255) { marker = i; break; }
        if (marker < 0) throw Bad();
        var edition = (int)footer[marker + 3];
        if (edition >= 106) edition -= 105;
        if (edition is not (2 or 6 or 30 or 31)) throw new InvalidDataException("Нужна GeoIP Legacy City; Country, ASN и другие .dat не содержат координаты города.");
        IsIPv6 = edition is 30 or 31;
        _segments = footer[marker + 4] | footer[marker + 5] << 8 | footer[marker + 6] << 16;
        _dataEnd = length - footer.Length + marker;
        if (_segments <= 0 || (long)_segments * 6 >= _dataEnd) throw Bad();
        var comment = Encoding.ASCII.GetString(footer, 0, marker);
        var date = Regex.Match(comment, @"Geo(?:Lite|IP)2[\s_-]+City[\s_-]+(\d{8})", RegexOptions.CultureInvariant);
        var dated = date.Success && DateTime.TryParseExact(date.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
        var build = dated ? DateTime.ParseExact(date.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) : File.GetLastWriteTimeUtc(path);
        Info = new("GeoIP Legacy City " + (IsIPv6 ? "IPv6" : "IPv4"), build)
        { TimeZoneSource = "Offline coordinates / GeoTimeZone 6.1.0", BuildDateSource = dated ? "database" : "file" };
        _map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        try { _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); }
        catch { _map.Dispose(); throw; }
    }

    public (double Latitude, double Longitude)? FindCoordinates(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IsIPv6 && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) address = address.MapToIPv6();
        var bytes = address.GetAddressBytes();
        if (bytes.Length != (IsIPv6 ? 16 : 4)) throw new InvalidDataException("Для IPv6 выхода установите GeoIPCityv6.dat.gz вместе с IPv4-базой.");
        var node = 0;
        for (var bit = 0; bit < bytes.Length * 8; bit++)
        {
            var right = (bytes[bit / 8] >> (7 - bit % 8)) & 1;
            var child = Read24((long)node * 6 + right * 3);
            if (child >= _segments) return ReadCoordinates(child);
            node = child;
        }
        throw Bad(); // Cycles or excessive tree depth cannot become an unbounded lookup.
    }

    /// <summary>Check every stored pointer on import; lookups are also bounds checked.</summary>
    public void ValidateTree()
    {
        var buffer = new byte[6 * 16384];
        for (var node = 0; node < _segments;)
        {
            var count = Math.Min(buffer.Length / 6, _segments - node);
            _view.ReadArray((long)node * 6, buffer, 0, count * 6);
            for (var i = 0; i < count * 6; i += 3)
            {
                var child = buffer[i] | buffer[i + 1] << 8 | buffer[i + 2] << 16;
                if (child > _segments && RecordOffset(child) + 10 > _dataEnd) throw Bad();
            }
            node += count;
        }
        var state = new byte[_segments];
        var height = new byte[_segments];
        var pending = new Stack<(int Node, int Depth, bool Exit)>();
        pending.Push((0, 0, false));
        var maximumDepth = IsIPv6 ? 128 : 32;
        while (pending.TryPop(out var item))
        {
            if (!item.Exit)
            {
                if (state[item.Node] == 1 || item.Depth >= maximumDepth) throw Bad();
                if (state[item.Node] == 2) { if (item.Depth + height[item.Node] > maximumDepth) throw Bad(); continue; }
                state[item.Node] = 1;
                pending.Push((item.Node, item.Depth, true));
                for (var side = 0; side < 2; side++)
                { var child = Read24((long)item.Node * 6 + side * 3); if (child < _segments) pending.Push((child, item.Depth + 1, false)); }
            }
            else
            {
                var longest = 1;
                for (var side = 0; side < 2; side++)
                { var child = Read24((long)item.Node * 6 + side * 3); if (child < _segments) longest = Math.Max(longest, height[child] + 1); }
                if (longest + item.Depth > maximumDepth) throw Bad();
                height[item.Node] = (byte)longest; state[item.Node] = 2;
            }
        }
    }

    private (double Latitude, double Longitude)? ReadCoordinates(int leaf)
    {
        if (leaf == _segments) return null;
        var position = RecordOffset(leaf);
        if (ReadByte(position++) == 0) return null; // Unknown country/location.
        for (var field = 0; field < 3; field++)
        {
            var terminated = false;
            for (var i = 0; i < 512; i++) if (ReadByte(position++) == 0) { terminated = true; break; }
            if (!terminated) throw Bad();
        }
        var latitude = Read24(position) / 10000.0 - 180;
        var longitude = Read24(position + 3) / 10000.0 - 180;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180 || latitude == 0 && longitude == 0) return null;
        return (latitude, longitude);
    }

    public string? FindTimeZone(IPAddress address)
    {
        if (FindCoordinates(address) is not { } coordinates) return null;
        var result = TimeZoneLookup.GetTimeZone(coordinates.Latitude, coordinates.Longitude);
        // Border cells can contain several zones; ocean/unknown cells have a longitude-only Etc/GMT fallback.
        if (result.AlternativeResults.Count != 0 || result.Result.StartsWith("Etc/GMT", StringComparison.Ordinal)
            || BrowserTimeZone.Validate(result.Result) is not null)
            throw new InvalidDataException("Координаты Legacy-базы не определяют однозначный часовой пояс. Обновите базу или выберите пояс вручную.");
        return result.Result;
    }
    private long RecordOffset(int leaf) => leaf + 5L * _segments;
    private int Read24(long position) => ReadByte(position) | ReadByte(position + 1) << 8 | ReadByte(position + 2) << 16;
    private byte ReadByte(long position) => position >= 0 && position < _dataEnd ? _view.ReadByte(position) : throw Bad();
    private static InvalidDataException Bad() => new("Повреждённая или неподдерживаемая GeoIP Legacy City база.");
    public void Dispose() { _view.Dispose(); _map.Dispose(); }
}
