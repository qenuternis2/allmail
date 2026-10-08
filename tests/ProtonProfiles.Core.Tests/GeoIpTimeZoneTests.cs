using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Tests;

public class GeoIpTimeZoneTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "GeoIP2-City-Test.mmdb");

    [Fact]
    public void Real_mmdb_resolves_ipv4_ipv6_offline_and_rejects_missing_or_conflicting_zones()
    {
        using var env = new TestEnv();
        var db = new GeoIpTimeZoneDatabase(env.Paths);
        Assert.Throws<FileNotFoundException>(() => db.Inspect());
        var info = db.Install(Fixture);
        Assert.Equal("GeoIP2-City", info.DatabaseType);
        Assert.Equal(info, db.Inspect());
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
        Assert.Equal("Asia/Tokyo", db.Resolve([IPAddress.Parse("2001:218::")]).TimeZoneId);
        Assert.Throws<InvalidDataException>(() => db.Resolve([IPAddress.Parse("81.2.69.160"), IPAddress.Parse("2001:218::")]));
        Assert.Throws<InvalidDataException>(() => db.Resolve([IPAddress.Parse("1.1.1.1")]));
        Assert.Throws<InvalidDataException>(() => db.Resolve([]));
    }

    [Fact]
    public void Invalid_update_keeps_installed_database_and_cleans_staging_files()
    {
        using var env = new TestEnv();
        var db = new GeoIpTimeZoneDatabase(env.Paths);
        db.Install(Fixture);
        var original = File.ReadAllBytes(db.InstalledFiles[0]);
        var invalid = Path.Combine(env.Root, "invalid.mmdb");
        File.WriteAllText(invalid, "not a database");
        Assert.ThrowsAny<Exception>(() => db.Install(invalid));
        Assert.Equal(original, File.ReadAllBytes(db.InstalledFiles[0]));
        Assert.Empty(Directory.GetFiles(env.Paths.GeoIpDirectory, "*.tmp"));
        db.Install(db.InstalledFiles[0]); // Updating from the installed file is also safe.
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.2")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.2")]
    [InlineData("100.64.1.2")]
    [InlineData("169.254.1.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")]
    [InlineData("3fff::1")]
    public void Non_public_egress_is_rejected(string ip) => Assert.False(GeoIpTimeZoneDatabase.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task Auto_mode_persists_roundtrips_and_requires_only_its_profile_to_restart()
    {
        using var env = new TestEnv();
        var original = env.AddProfile();
        var other = env.AddProfile("Other");
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(original.Id);
        await lifecycle.OpenAsync(other.Id);
        var automatic = original with { BrowserTimeZoneAuto = true };
        Assert.True(env.Catalog.SaveSettings(automatic, profileIsLive: true).RestartRequired);
        await lifecycle.RestartAsync(original.Id);
        Assert.True(env.Engine.Requests[^1].Config.BrowserTimeZoneAuto);
        Assert.Equal(0, env.Engine.Sessions[1].CloseCalls);
        Assert.True(new SqliteProfileRepository(env.Paths.DatabasePath).Get(original.Id)!.BrowserTimeZoneAuto);
        var json = SettingsInterchange.Export([automatic]);
        Assert.True(SettingsInterchange.Import(Encoding.UTF8.GetBytes(json)).Preview!.Profiles[0].BrowserTimeZoneAuto);
        Assert.Contains("browserTimeZoneAuto", json);
        Assert.DoesNotContain("GeoLite2-City.mmdb", json);
        Assert.NotEmpty(ProfileValidator.Validate(automatic with { BrowserTimeZoneId = "Europe/London" }));
        var document = JsonNode.Parse(json)!;
        document["profiles"]![0]!["browserTimeZoneId"] = "Europe/London";
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(document.ToJsonString())).Success);
        document["profiles"]![0]!["browserTimeZoneId"] = null;
        document["profiles"]![0]!["browserTimeZoneAuto"] = "true";
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(document.ToJsonString())).Success);
        document["profiles"]![0]!.AsObject().Remove("browserTimeZoneAuto");
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(document.ToJsonString())).Preview!.Profiles[0].BrowserTimeZoneAuto);
    }

    [Fact]
    public void V6_migration_preserves_manual_timezone_browser_data_and_backups()
    {
        using var env = new TestEnv();
        var original = env.AddProfile(change: p => p with { BrowserTimeZoneId = "Europe/London" });
        Directory.CreateDirectory(env.Paths.UserDataFolder(original.Id));
        var marker = Path.Combine(env.Paths.UserDataFolder(original.Id), "cookies-marker");
        File.WriteAllText(marker, "keep");
        using (var c = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE Profile DROP COLUMN ReputationCheckingEnabled; ALTER TABLE Profile DROP COLUMN BrowserTimeZoneAuto; PRAGMA user_version=6; UPDATE ProfileRevision SET Snapshot=json_remove(Snapshot,'$.BrowserTimeZoneAuto');";
            cmd.ExecuteNonQuery();
        }
        var migrated = new SqliteProfileRepository(env.Paths.DatabasePath, env.Paths.BackupsRoot);
        Assert.Equal(original, migrated.Get(original.Id));
        Assert.False(migrated.GetRevisionSnapshot(original.Id, 1)!.BrowserTimeZoneAuto);
        Assert.Single(Directory.GetFiles(env.Paths.BackupsRoot, "profiles.v6.*.db"));
        Assert.Equal("keep", File.ReadAllText(marker));
    }
}
