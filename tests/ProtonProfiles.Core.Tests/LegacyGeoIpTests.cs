using System.Net;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class LegacyGeoIpTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-city", name);

    [Fact]
    public void Legacy_gzip_pair_maps_coordinates_offline_and_rejects_conflicting_zones()
    {
        using var env = new TestEnv(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        var info = db.Install(Fixture("GeoIPCityv6.dat.gz"), Fixture("GeoIPCity.dat.gz"));
        Assert.Contains("IPv4 + IPv6", info.DatabaseType);
        Assert.Equal(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), info.BuildDate);
        Assert.Contains("GeoTimeZone 6.1.0", info.TimeZoneSource);
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
        Assert.Equal("Asia/Tokyo", db.Resolve([IPAddress.Parse("2001:218::")]).TimeZoneId);
        Assert.Throws<InvalidDataException>(() => db.Resolve([IPAddress.Parse("81.2.69.160"), IPAddress.Parse("2001:218::")]));
        Assert.Throws<InvalidDataException>(() => db.Resolve([IPAddress.Parse("1.1.1.1")]));
        Assert.Equal(info, new GeoIpTimeZoneDatabase(env.Paths).Inspect());
    }

    [Fact]
    public void Ipv4_only_never_ignores_ipv6_and_ipv6_database_supports_mapped_ipv4()
    {
        using var env = new TestEnv(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        db.Install(Fixture("GeoIPCity.dat"));
        Assert.Contains("GeoIPCityv6", Assert.Throws<InvalidDataException>(() => db.Resolve([IPAddress.Parse("81.2.69.160"), IPAddress.Parse("2001:218::")])).Message);
        db.Install(Fixture("GeoIPCityv6.dat.gz"));
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("::ffff:81.2.69.160")]).TimeZoneId);
    }

    [Fact]
    public void Failed_pair_update_country_edition_and_corrupt_gzip_preserve_active_generation()
    {
        using var env = new TestEnv(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        db.Install(Fixture("GeoIPCity.dat")); var files = db.InstalledFiles.ToArray();
        var bad = Path.Combine(env.Root, "country.dat"); var bytes = File.ReadAllBytes(Fixture("GeoIPCity.dat"));
        bytes[^4] = 1; File.WriteAllBytes(bad, bytes);
        Assert.Throws<InvalidDataException>(() => db.Install(Fixture("GeoIPCity.dat.gz"), bad));
        Assert.Equal(files, db.InstalledFiles);
        var gzip = Path.Combine(env.Root, "corrupt.dat.gz"); File.WriteAllText(gzip, "invalid gzip");
        Assert.Throws<InvalidDataException>(() => db.Install(gzip));
        Assert.Throws<InvalidDataException>(() => db.Install(Fixture("GeoIPCity.dat"), Fixture("GeoIPCity.dat.gz")));
        Assert.Equal(files, db.InstalledFiles);
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
        Assert.Empty(Directory.GetFiles(env.Paths.GeoIpDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Corrupt_pointers_and_tree_cycles_are_rejected_before_install()
    {
        using var env = new TestEnv(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        var bad = Path.Combine(env.Root, "bad.dat"); var bytes = File.ReadAllBytes(Fixture("GeoIPCity.dat"));
        bytes[0] = bytes[1] = bytes[2] = 0; File.WriteAllBytes(bad, bytes);
        Assert.Throws<InvalidDataException>(() => db.Install(bad));
        bytes[0] = bytes[1] = bytes[2] = 0xff; File.WriteAllBytes(bad, bytes);
        Assert.Throws<InvalidDataException>(() => db.Install(bad));
        Assert.Throws<FileNotFoundException>(() => db.Inspect());
    }

    [Fact]
    public void Old_mmdb_installation_is_read_without_conversion_and_formats_can_switch()
    {
        using var env = new TestEnv(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        Directory.CreateDirectory(env.Paths.GeoIpDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "GeoIP2-City-Test.mmdb"), env.Paths.GeoIpDatabasePath);
        Assert.Equal("GeoIP2-City", db.Inspect().DatabaseType);
        db.Install(Fixture("GeoIPCity.dat.gz"));
        Assert.StartsWith("GeoIP Legacy", db.Inspect().DatabaseType);
        db.Install(env.Paths.GeoIpDatabasePath);
        Assert.Equal("GeoIP2-City", db.Inspect().DatabaseType);
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
    }

}
