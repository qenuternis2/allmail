using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;

namespace ProtonProfiles.Core.Tests;

public class RepositoryTests
{
    [Fact]
    public void Roundtrips_all_fields()
    {
        using var env = new TestEnv();
        ProxyEndpoint.TryCreate("http", "proxy.test", 3128, out var ep, out _);
        var p = new ProfileConfig
        {
            Id = Guid.NewGuid(), DisplayName = "Full", EmailLabel = "label", Color = "#ABCDEF", SortOrder = 7, IsFavorite = true, IsPinned = true,
            ConfigRevision = 3, LastAppliedRevision = 2, PendingRevision = 3, NetworkMode = NetworkMode.Proxy,
            Proxy = new ProxySettings(ep, ProxyAuthMode.Basic, "ref"), UserAgentMode = UserAgentMode.Custom, CustomUserAgent = "UA",
            LanguageMode = LanguageMode.Custom, LanguageTag = "en-US", ScriptLocaleMode = ScriptLocaleMode.Custom, ScriptLocaleTag = "de-DE",
            Kind = ProfileKind.Test, TestStartUrl = "https://example.test/check?token=synthetic", GraphicsPolicy = GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental,
            BrowserTimeZoneId = "Europe/Berlin",
            ColorScheme = ColorSchemePreference.Dark, ZoomFactor = 1.25, WindowBounds = new WindowBounds(1, 2, 300, 400, true),
            TrackingPreventionLevel = TrackingPreventionLevel.Strict, DownloadDirectory = "/x", LastOpenedAt = DateTimeOffset.UnixEpoch.AddDays(1),
            LastUserConfirmedVisitAt = DateTimeOffset.UnixEpoch.AddDays(2), ConfirmationLocalDate = new DateOnly(2026, 1, 31),
            ConfirmationTimeZoneId = "Europe/Moscow", ReminderMonths = 3, SnoozedUntil = DateTimeOffset.UnixEpoch.AddDays(3),
        };
        env.Repository.Insert(p);
        Assert.Equal(p, env.Repository.Get(p.Id));
    }

    [Fact]
    public void Proxy_with_omitted_endpoint_roundtrips_as_null_endpoint()
    {
        using var env = new TestEnv();
        var p = new ProfileConfig { Id = Guid.NewGuid(), DisplayName = "P", NetworkMode = NetworkMode.Proxy, Proxy = new ProxySettings(null, ProxyAuthMode.None, null) };
        env.Repository.Insert(p);
        var back = env.Repository.Get(p.Id)!;
        Assert.NotNull(back.Proxy);
        Assert.Null(back.Proxy!.Endpoint);
    }

    [Fact]
    public void Uuid_reuse_is_rejected()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        Assert.Throws<SqliteException>(() => env.Repository.Insert(a with { DisplayName = "dup" }));
    }

    [Fact]
    public void Delete_cascades_but_keeps_pending_operations()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        env.Repository.AddVisitConfirmation(a.Id, DateTimeOffset.UtcNow, new DateOnly(2026, 1, 1), "UTC");
        env.Repository.SetPermission(new PermissionDecision(a.Id, "https://mail.proton.me", PermissionKindKey.Camera, PermissionChoice.Deny, DateTimeOffset.UtcNow));
        var op = env.Repository.AddPendingOperation(a.Id, PendingOperationKind.Delete, "Cleaning");
        env.Repository.DeleteMetadata(a.Id);
        Assert.Empty(env.Repository.ListVisitConfirmations(a.Id));
        Assert.Empty(env.Repository.ListPermissions(a.Id));
        Assert.Single(env.Repository.ListPendingOperations());
        env.Repository.CompletePendingOperation(op);
        Assert.Empty(env.Repository.ListPendingOperations());
    }

    [Fact]
    public void Newer_schema_is_refused_without_changes()
    {
        using var env = new TestEnv();
        env.AddProfile("Keep");
        using (var c = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<SchemaMigrationException>(() => new SqliteProfileRepository(env.Paths.DatabasePath, env.Paths.BackupsRoot));
        using (var c = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DisplayName FROM Profile;";
            Assert.Equal("Keep", cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void Failed_migration_rolls_back_and_backs_up_original()
    {
        using var env = new TestEnv();
        env.AddProfile("Keep");
        var broken = SqliteProfileRepository.Migrations.Concat([["ALTER TABLE Profile ADD COLUMN X INTEGER;", "THIS IS NOT SQL;"]]).ToList();
        var ex = Assert.Throws<SchemaMigrationException>(() => env.Repository.MigrateWith(broken));
        Assert.NotNull(ex.BackupPath);
        Assert.True(File.Exists(ex.BackupPath));

        using var c = new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False");
        c.Open();
        Assert.Equal(SqliteProfileRepository.CurrentSchemaVersion, SqliteProfileRepository.ReadSchemaVersion(c));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Profile') WHERE name = 'X';";
        Assert.Equal(0L, cmd.ExecuteScalar()); // partial DDL rolled back
        Assert.Equal("Keep", env.Repository.ListProfiles().Single().DisplayName);
    }

    [Fact]
    public void Hundred_synthetic_profiles_search_within_budget()
    {
        using var env = new TestEnv();
        var profiles = Enumerable.Range(0, 100).Select(i => new ProfileConfig
        {
            Id = Guid.NewGuid(), DisplayName = $"Синтетический профиль {i:D3}", EmailLabel = $"synthetic{i}@example.invalid", SortOrder = i,
        }).ToList();
        env.Repository.InsertMany(profiles);

        var samples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var sw = Stopwatch.StartNew();
            var list = env.Catalog.List();
            var hits = ProfileCatalog.Filter(list, $"{i:D2}").ToList();
            _ = env.Repository.Get(list[i].Id);
            sw.Stop();
            Assert.NotEmpty(hits);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(0.95 * samples.Count) - 1];
        Assert.True(p95 < 200, $"p95 = {p95:F1} ms");
    }
}
