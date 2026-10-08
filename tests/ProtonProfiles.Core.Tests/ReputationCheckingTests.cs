using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;

namespace ProtonProfiles.Core.Tests;

public class ReputationCheckingTests
{
    [Fact]
    public void New_profiles_and_old_snapshots_enable_reputation_checks()
    {
        var profile = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Default" };
        Assert.True(profile.ReputationCheckingEnabled);
        var old = JsonNode.Parse(JsonSerializer.Serialize(profile))!;
        old.AsObject().Remove(nameof(ProfileConfig.ReputationCheckingEnabled));
        Assert.True(JsonSerializer.Deserialize<ProfileConfig>(old.ToJsonString())!.ReputationCheckingEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Preference_roundtrips_database_snapshot_and_settings_export(bool enabled)
    {
        using var env = new TestEnv();
        var profile = env.AddProfile(change: p => p with { ReputationCheckingEnabled = enabled });
        var reopened = new SqliteProfileRepository(env.Paths.DatabasePath);
        Assert.Equal(profile, reopened.Get(profile.Id));
        var revision = reopened.SaveRevisionSnapshot(profile);
        Assert.Equal(enabled, reopened.GetRevisionSnapshot(profile.Id, revision)!.ReputationCheckingEnabled);
        var exported = SettingsInterchange.Export([profile]);
        Assert.True(InterchangeTests.SchemaValid(exported));
        var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(exported));
        Assert.True(imported.Success);
        Assert.Equal(enabled, imported.Preview!.Profiles.Single().ReputationCheckingEnabled);
        Assert.Equal(!enabled, imported.Preview.Notes.Any(n => n.Contains("SmartScreen", StringComparison.Ordinal)));
    }

    [Fact]
    public void Old_export_keeps_checks_enabled()
    {
        var doc = JsonNode.Parse(SettingsInterchange.Export([new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Old" }]))!;
        doc["profiles"]![0]!.AsObject().Remove("reputationCheckingEnabled");
        Assert.True(InterchangeTests.SchemaValid(doc.ToJsonString()));
        var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        Assert.True(imported.Success);
        Assert.True(imported.Preview!.Profiles.Single().ReputationCheckingEnabled);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"false\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Malformed_preferences_reject_whole_import(string invalid)
    {
        var doc = JsonNode.Parse(SettingsInterchange.Export([new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "Invalid" }]))!;
        doc["profiles"]![0]!["reputationCheckingEnabled"] = JsonNode.Parse(invalid);
        Assert.False(InterchangeTests.SchemaValid(doc.ToJsonString()));
        var imported = SettingsInterchange.Import(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        Assert.False(imported.Success);
        Assert.Null(imported.Preview);
        Assert.Contains(imported.Errors, e => e.Path == "$.profiles[0].reputationCheckingEnabled");
    }

    [Fact]
    public void V7_migration_enables_checks_without_changing_existing_data()
    {
        using var env = new TestEnv();
        var profile = env.AddProfile("Preserved");
        env.Repository.SetPermission(new(profile.Id, "https://example.test", PermissionKindKey.Notifications, PermissionChoice.Allow, DateTimeOffset.UnixEpoch));
        var group = env.Repository.CreateGroup("Group");
        env.Repository.AssignGroup([profile.Id], group.Id);
        Directory.CreateDirectory(env.Paths.UserDataFolder(profile.Id));
        var marker = Path.Combine(env.Paths.UserDataFolder(profile.Id), "session-marker");
        File.WriteAllText(marker, "preserved");
        using (var db = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "ALTER TABLE Profile DROP COLUMN ReputationCheckingEnabled; PRAGMA user_version=7; UPDATE ProfileRevision SET Snapshot=json_remove(Snapshot,'$.ReputationCheckingEnabled');";
            command.ExecuteNonQuery();
        }
        var migrated = new SqliteProfileRepository(env.Paths.DatabasePath, env.Paths.BackupsRoot);
        Assert.Equal(profile, migrated.Get(profile.Id));
        Assert.True(migrated.GetRevisionSnapshot(profile.Id, 1)!.ReputationCheckingEnabled);
        Assert.Single(migrated.ListPermissions(profile.Id));
        Assert.Equal(group.Id, migrated.ListGroupAssignments()[profile.Id]);
        Assert.Equal("preserved", File.ReadAllText(marker));
        var backup = Assert.Single(Directory.GetFiles(env.Paths.BackupsRoot, "profiles.v7.*.db"));
        using var old = new SqliteConnection($"Data Source={backup};Pooling=False");
        old.Open();
        Assert.Equal(7, SqliteProfileRepository.ReadSchemaVersion(old));
    }

    [Fact]
    public async Task Editing_live_profile_waits_for_restart_and_does_not_affect_another_profile()
    {
        using var env = new TestEnv();
        var a = env.AddProfile("A");
        var b = env.AddProfile("B");
        var lifecycle = env.Lifecycle();
        Assert.Equal(OpenOutcome.Opened, (await lifecycle.OpenAsync(a.Id)).Outcome);
        Assert.Equal(OpenOutcome.Opened, (await lifecycle.OpenAsync(b.Id)).Outcome);
        var disabled = a with { ReputationCheckingEnabled = false };
        Assert.True(env.Catalog.SaveSettings(disabled, profileIsLive: true).RestartRequired);
        Assert.True(env.Engine.Requests[0].Config.ReputationCheckingEnabled);
        Assert.Equal(OpenOutcome.Opened, (await lifecycle.RestartAsync(a.Id)).Outcome);
        Assert.False(env.Engine.Requests[^1].Config.ReputationCheckingEnabled);
        Assert.True(env.Repository.Get(b.Id)!.ReputationCheckingEnabled);
        Assert.Equal(0, env.Engine.Sessions[1].CloseCalls);
        Assert.True(ProfileConfig.RequiresRestart(disabled, a));
    }
}
