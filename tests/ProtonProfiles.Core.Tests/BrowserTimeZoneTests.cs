using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class BrowserTimeZoneTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("UTC")]
    [InlineData("Europe/Berlin")]
    [InlineData("America/New_York")]
    [InlineData("Africa/Nairobi")]
    public void Supported_IANA_ids_and_host_default_are_valid(string? id) => Assert.Null(BrowserTimeZone.Validate(id));

    [Theory]
    [InlineData("")]
    [InlineData("Not/AZone")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("Europe/Berlin\n")]
    [InlineData("UTC --disable-web-security")]
    [InlineData("UTC';alert(1)")]
    public void Invalid_or_injected_ids_are_rejected_by_validation_and_import(string id)
    {
        Assert.NotNull(BrowserTimeZone.Validate(id));
        var doc = JsonNode.Parse(SettingsInterchange.Export([new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "P" }]))!;
        doc["profiles"]![0]!["browserTimeZoneId"] = id;
        Assert.False(SettingsInterchange.Import(Encoding.UTF8.GetBytes(doc.ToJsonString())).Success);
    }

    [Fact]
    public void Berlin_readback_checks_native_Date_winter_and_summer_offsets()
    {
        var script = BrowserTimeZone.VerificationScript("Europe/Berlin");
        Assert.Contains("new Date(d.epoch).getTimezoneOffset()", script);
        Assert.Contains("resolvedOptions().timeZone === expected", script);
        var json = script[script.IndexOf("[{", StringComparison.Ordinal)..(script.IndexOf("].every", StringComparison.Ordinal) + 1)];
        using var samples = JsonDocument.Parse(json);
        Assert.Equal(-60, samples.RootElement[0].GetProperty("offset").GetInt32());
        Assert.Equal(-120, samples.RootElement[1].GetProperty("offset").GetInt32());
    }

    [Fact]
    public void Export_import_preserves_zone_and_old_exports_keep_host_default()
    {
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "P", BrowserTimeZoneId = "Europe/Berlin" };
        var doc = JsonNode.Parse(SettingsInterchange.Export([p]))!;
        Assert.Equal(p.BrowserTimeZoneId, SettingsInterchange.Import(Encoding.UTF8.GetBytes(doc.ToJsonString())).Preview!.Profiles[0].BrowserTimeZoneId);
        doc["profiles"]![0]!.AsObject().Remove("browserTimeZoneId");
        Assert.Null(SettingsInterchange.Import(Encoding.UTF8.GetBytes(doc.ToJsonString())).Preview!.Profiles[0].BrowserTimeZoneId);
    }

    [Fact]
    public async Task Changing_zone_restarts_only_its_profile_and_keeps_reminder_basis()
    {
        using var env = new TestEnv();
        var a = env.AddProfile(change: p => p with { ConfirmationTimeZoneId = "Africa/Nairobi", ConfirmationLocalDate = new DateOnly(2026, 10, 3) });
        var b = env.AddProfile("B");
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(a.Id);
        await lifecycle.OpenAsync(b.Id);
        var saved = env.Catalog.SaveSettings(a with { BrowserTimeZoneId = "Europe/Berlin" }, profileIsLive: true);
        Assert.True(saved.RestartRequired);
        Assert.Null(env.Engine.Requests[0].Config.BrowserTimeZoneId);
        await lifecycle.RestartAsync(a.Id);
        Assert.Equal("Europe/Berlin", env.Engine.Requests[^1].Config.BrowserTimeZoneId);
        Assert.Equal(0, env.Engine.Sessions[1].CloseCalls);
        Assert.Equal(a.ConfirmationTimeZoneId, env.Repository.Get(a.Id)!.ConfirmationTimeZoneId);
        Assert.Equal(a.ConfirmationLocalDate, env.Repository.Get(a.Id)!.ConfirmationLocalDate);
        Assert.True(ProfileConfig.RequiresRestart(a with { BrowserTimeZoneId = "Europe/Berlin" }, a));
    }

    [Fact]
    public void V2_migration_preserves_profiles_permissions_snapshots_and_browser_data()
    {
        using var env = new TestEnv();
        var p = env.AddProfile("Keep");
        env.Repository.SetPermission(new PermissionDecision(p.Id, "https://mail.proton.me", PermissionKindKey.Notifications, PermissionChoice.Allow, DateTimeOffset.UnixEpoch));
        Directory.CreateDirectory(env.Paths.UserDataFolder(p.Id));
        var marker = Path.Combine(env.Paths.UserDataFolder(p.Id), "session-marker");
        File.WriteAllText(marker, "keep");
        using (var c = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE ProfileGroupMember; DROP TABLE ProfileGroup; ALTER TABLE Profile DROP COLUMN BrowserTimeZoneAuto; ALTER TABLE Profile DROP COLUMN PrivacyExceptions; ALTER TABLE Profile DROP COLUMN Kind; ALTER TABLE Profile DROP COLUMN TestStartUrl; ALTER TABLE Profile DROP COLUMN GraphicsPolicy; ALTER TABLE Profile DROP COLUMN BrowserTimeZoneId; PRAGMA user_version = 2; UPDATE ProfileRevision SET Snapshot = json_remove(Snapshot, '$.BrowserTimeZoneId');";
            cmd.ExecuteNonQuery();
        }
        var migrated = new SqliteProfileRepository(env.Paths.DatabasePath, env.Paths.BackupsRoot);
        Assert.Equal(p, migrated.Get(p.Id));
        Assert.Null(migrated.GetRevisionSnapshot(p.Id, 1)!.BrowserTimeZoneId);
        Assert.Single(migrated.ListPermissions(p.Id));
        Assert.Equal("keep", File.ReadAllText(marker));
        var backup = Assert.Single(Directory.GetFiles(env.Paths.BackupsRoot, "profiles.v2.*.db"));
        using var backupConnection = new SqliteConnection($"Data Source={backup};Pooling=False");
        backupConnection.Open();
        Assert.Equal(2, SqliteProfileRepository.ReadSchemaVersion(backupConnection));
        migrated.Update(p with { BrowserTimeZoneId = "Europe/Berlin" });
        Assert.Equal("Europe/Berlin", new SqliteProfileRepository(env.Paths.DatabasePath).Get(p.Id)!.BrowserTimeZoneId);
    }
}
