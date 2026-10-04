using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Reminders;

namespace ProtonProfiles.Core.Persistence;

public sealed class SchemaMigrationException : Exception
{
    public string? BackupPath { get; }
    public SchemaMigrationException(string message, string? backupPath, Exception? inner = null) : base(message, inner) => BackupPath = backupPath;
}

/// <summary>
/// Versioned SQLite metadata store (spec §9). Each migration runs in a transaction after a pre-migration backup;
/// a failed migration leaves the original file intact and is never "fixed" by recreating an empty database.
/// </summary>
public sealed partial class SqliteProfileRepository : IProfileRepository
{
    public const int CurrentSchemaVersion = 5;

    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly string? _backupDirectory;
    private readonly object _sync = new();

    internal static readonly IReadOnlyList<string[]> Migrations =
    [
        // v1
        [
            """
            CREATE TABLE Profile (
                Id TEXT PRIMARY KEY NOT NULL,
                DisplayName TEXT NOT NULL,
                EmailLabel TEXT NULL,
                Color TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                IsFavorite INTEGER NOT NULL,
                IsPinned INTEGER NOT NULL,
                ConfigRevision INTEGER NOT NULL,
                LastAppliedRevision INTEGER NULL,
                PendingRevision INTEGER NULL,
                NetworkMode INTEGER NOT NULL,
                ProxyHost TEXT NULL,
                ProxyPort INTEGER NULL,
                ProxyType INTEGER NULL,
                ProxyAuthMode INTEGER NULL,
                ProxyCredentialRef TEXT NULL,
                ProxyConfigured INTEGER NOT NULL DEFAULT 0,
                UserAgentMode INTEGER NOT NULL,
                CustomUserAgent TEXT NULL,
                LanguageMode INTEGER NOT NULL,
                LanguageTag TEXT NULL,
                ScriptLocaleMode INTEGER NOT NULL,
                ScriptLocaleTag TEXT NULL,
                ColorScheme INTEGER NOT NULL,
                ZoomFactor REAL NOT NULL,
                WindowBounds TEXT NULL,
                TrackingPreventionLevel INTEGER NOT NULL,
                DownloadDirectory TEXT NULL,
                LastOpenedAt TEXT NULL,
                LastUserConfirmedVisitAt TEXT NULL,
                ConfirmationLocalDate TEXT NULL,
                ConfirmationTimeZoneId TEXT NULL,
                ReminderMonths INTEGER NOT NULL,
                SnoozedUntil TEXT NULL
            );
            """,
            """
            CREATE TABLE ProfileRevision (
                ProfileId TEXT NOT NULL REFERENCES Profile(Id) ON DELETE CASCADE,
                Revision INTEGER NOT NULL,
                Snapshot TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (ProfileId, Revision)
            );
            """,
            """
            CREATE TABLE VisitConfirmation (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId TEXT NOT NULL REFERENCES Profile(Id) ON DELETE CASCADE,
                ConfirmedAt TEXT NOT NULL,
                LocalDate TEXT NOT NULL,
                TimeZoneId TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE PermissionDecision (
                ProfileId TEXT NOT NULL REFERENCES Profile(Id) ON DELETE CASCADE,
                Origin TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                Choice INTEGER NOT NULL,
                DecidedAt TEXT NOT NULL,
                PRIMARY KEY (ProfileId, Origin, Kind)
            );
            """,
            // No FK: a delete intent must survive deletion of the profile row until cleanup is complete.
            """
            CREATE TABLE PendingOperation (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                State TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Detail TEXT NULL,
                CompletedAt TEXT NULL
            );
            """,
        ],
        // v2: existing profiles block page APIs by default; no browser data or permission records are deleted.
        [
            "ALTER TABLE Profile ADD COLUMN WebRtcPagePolicy INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Profile ADD COLUMN WebRtcNetworkPolicy INTEGER NOT NULL DEFAULT 0;",
        ],
        // v3: existing profiles keep their host time zone; browser data and reminder basis are untouched.
        ["ALTER TABLE Profile ADD COLUMN BrowserTimeZoneId TEXT NULL;"],
        // v4: mail profiles and native graphics remain the defaults for existing data.
        [
            "ALTER TABLE Profile ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Profile ADD COLUMN TestStartUrl TEXT NULL;",
            "ALTER TABLE Profile ADD COLUMN GraphicsPolicy INTEGER NOT NULL DEFAULT 0;",
        ],
        // v5: independent local grouping metadata; browser configuration/revisions stay unchanged.
        [
            "CREATE TABLE ProfileGroup (Id TEXT PRIMARY KEY NOT NULL, Name TEXT NOT NULL, NameKey TEXT NOT NULL UNIQUE, SortOrder INTEGER NOT NULL);",
            "CREATE TABLE ProfileGroupMember (ProfileId TEXT PRIMARY KEY NOT NULL REFERENCES Profile(Id) ON DELETE CASCADE, GroupId TEXT NOT NULL REFERENCES ProfileGroup(Id) ON DELETE CASCADE);",
            "CREATE INDEX IX_ProfileGroupMember_GroupId ON ProfileGroupMember(GroupId);",
        ],
    ];

    public const int VisitHistoryLimit = 50;

    public SqliteProfileRepository(string databasePath, string? backupDirectory = null)
    {
        _databasePath = databasePath;
        _backupDirectory = backupDirectory;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public static int ReadSchemaVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Migrate() => MigrateWith(Migrations);

    internal void MigrateWith(IReadOnlyList<string[]> migrations)
    {
        lock (_sync)
        {
            using var c = Open();
            var version = ReadSchemaVersion(c);
            if (version > migrations.Count)
                throw new SchemaMigrationException($"База данных создана более новой версией приложения (схема {version}).", null);
            if (version == migrations.Count) return;

            string? backup = null;
            if (version > 0 && _backupDirectory is not null)
            {
                Directory.CreateDirectory(_backupDirectory);
                backup = Path.Combine(_backupDirectory, $"profiles.v{version}.{DateTime.UtcNow:yyyyMMddHHmmss}.db");
                using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
                dest.Open();
                c.BackupDatabase(dest);
            }

            for (var v = version; v < migrations.Count; v++)
            {
                using var tx = c.BeginTransaction();
                try
                {
                    foreach (var sql in migrations[v])
                    {
                        using var cmd = c.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = sql;
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = $"PRAGMA user_version = {v + 1};";
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                catch (Exception e)
                {
                    tx.Rollback();
                    throw new SchemaMigrationException($"Не удалось обновить схему базы данных до версии {v + 1}. Исходные данные сохранены.", backup, e);
                }
            }
        }
    }

    public IReadOnlyList<ProfileConfig> ListProfiles()
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM Profile ORDER BY SortOrder, DisplayName;";
            using var r = cmd.ExecuteReader();
            var list = new List<ProfileConfig>();
            while (r.Read()) list.Add(ReadProfile(r));
            return list;
        }
    }

    public ProfileConfig? Get(Guid id)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM Profile WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadProfile(r) : null;
        }
    }

    public void Insert(ProfileConfig profile) => InsertMany([profile]);

    public void InsertMany(IReadOnlyList<ProfileConfig> profiles)
    {
        lock (_sync)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var p in profiles)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO Profile ({string.Join(",", Columns)}) VALUES ({string.Join(",", Columns.Select(x => "$" + x))});";
                Bind(cmd, p);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Update(ProfileConfig profile)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE Profile SET {string.Join(",", Columns.Where(x => x != "Id").Select(x => $"{x} = ${x}"))} WHERE Id = $Id;";
            Bind(cmd, profile);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Profile not found.");
        }
    }

    public void DeleteMetadata(Guid id) => Exec("DELETE FROM Profile WHERE Id = $id;", ("$id", id.ToString("D")));

    public void Reorder(IReadOnlyList<Guid> orderedIds)
    {
        lock (_sync)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            for (var i = 0; i < orderedIds.Count; i++)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Profile SET SortOrder = $o WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$o", i);
                cmd.Parameters.AddWithValue("$id", orderedIds[i].ToString("D"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public long AddVisitConfirmation(Guid profileId, DateTimeOffset atUtc, DateOnly localDate, string timeZoneId)
    {
        lock (_sync)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO VisitConfirmation (ProfileId, ConfirmedAt, LocalDate, TimeZoneId) VALUES ($p, $at, $d, $tz); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", profileId.ToString("D"));
            cmd.Parameters.AddWithValue("$at", Iso(atUtc));
            cmd.Parameters.AddWithValue("$d", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$tz", timeZoneId);
            var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            using var trim = c.CreateCommand();
            trim.Transaction = tx;
            // Bounded local history (spec §8).
            trim.CommandText = "DELETE FROM VisitConfirmation WHERE ProfileId = $p AND Id NOT IN (SELECT Id FROM VisitConfirmation WHERE ProfileId = $p ORDER BY Id DESC LIMIT $n);";
            trim.Parameters.AddWithValue("$p", profileId.ToString("D"));
            trim.Parameters.AddWithValue("$n", VisitHistoryLimit);
            trim.ExecuteNonQuery();
            tx.Commit();
            return id;
        }
    }

    public IReadOnlyList<VisitConfirmation> ListVisitConfirmations(Guid profileId)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id, ConfirmedAt, LocalDate, TimeZoneId FROM VisitConfirmation WHERE ProfileId = $p ORDER BY Id DESC;";
            cmd.Parameters.AddWithValue("$p", profileId.ToString("D"));
            using var r = cmd.ExecuteReader();
            var list = new List<VisitConfirmation>();
            while (r.Read())
                list.Add(new VisitConfirmation(r.GetInt64(0), profileId, ParseTime(r.GetString(1))!.Value, DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetString(3)));
            return list;
        }
    }

    public void DeleteVisitConfirmation(long id) => Exec("DELETE FROM VisitConfirmation WHERE Id = $id;", ("$id", id));

    public PermissionDecision? GetPermission(Guid profileId, string origin, PermissionKindKey kind)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Choice, DecidedAt FROM PermissionDecision WHERE ProfileId = $p AND Origin = $o AND Kind = $k;";
            cmd.Parameters.AddWithValue("$p", profileId.ToString("D"));
            cmd.Parameters.AddWithValue("$o", origin);
            cmd.Parameters.AddWithValue("$k", (int)kind);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new PermissionDecision(profileId, origin, kind, (PermissionChoice)r.GetInt32(0), ParseTime(r.GetString(1))!.Value) : null;
        }
    }

    public void SetPermission(PermissionDecision d) => Exec(
        "INSERT INTO PermissionDecision (ProfileId, Origin, Kind, Choice, DecidedAt) VALUES ($p, $o, $k, $c, $t) ON CONFLICT(ProfileId, Origin, Kind) DO UPDATE SET Choice = excluded.Choice, DecidedAt = excluded.DecidedAt;",
        ("$p", d.ProfileId.ToString("D")), ("$o", d.Origin), ("$k", (int)d.Kind), ("$c", (int)d.Choice), ("$t", Iso(d.DecidedAtUtc)));

    public void DeletePermissions(Guid profileId) => Exec("DELETE FROM PermissionDecision WHERE ProfileId = $p;", ("$p", profileId.ToString("D")));

    public IReadOnlyList<PermissionDecision> ListPermissions(Guid profileId)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Origin, Kind, Choice, DecidedAt FROM PermissionDecision WHERE ProfileId = $p ORDER BY Origin, Kind;";
            cmd.Parameters.AddWithValue("$p", profileId.ToString("D"));
            using var r = cmd.ExecuteReader();
            var list = new List<PermissionDecision>();
            while (r.Read()) list.Add(new PermissionDecision(profileId, r.GetString(0), (PermissionKindKey)r.GetInt32(1), (PermissionChoice)r.GetInt32(2), ParseTime(r.GetString(3))!.Value));
            return list;
        }
    }

    public long AddPendingOperation(Guid profileId, PendingOperationKind kind, string state, string? detail = null)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO PendingOperation (ProfileId, Kind, State, CreatedAt, Detail) VALUES ($p, $k, $s, $t, $d); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", profileId.ToString("D"));
            cmd.Parameters.AddWithValue("$k", (int)kind);
            cmd.Parameters.AddWithValue("$s", state);
            cmd.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public void UpdatePendingOperation(long id, string state, string? detail = null) =>
        Exec("UPDATE PendingOperation SET State = $s, Detail = $d WHERE Id = $id;", ("$s", state), ("$d", detail), ("$id", id));

    public void CompletePendingOperation(long id) =>
        Exec("UPDATE PendingOperation SET State = 'Completed', CompletedAt = $t WHERE Id = $id;", ("$t", Iso(DateTimeOffset.UtcNow)), ("$id", id));

    public IReadOnlyList<PendingOperation> ListPendingOperations()
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id, ProfileId, Kind, State, CreatedAt, Detail FROM PendingOperation WHERE CompletedAt IS NULL ORDER BY Id;";
            using var r = cmd.ExecuteReader();
            var list = new List<PendingOperation>();
            while (r.Read())
                list.Add(new PendingOperation(r.GetInt64(0), Guid.Parse(r.GetString(1)), (PendingOperationKind)r.GetInt32(2), r.GetString(3), ParseTime(r.GetString(4))!.Value, r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        }
    }

    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = false };

    public long SaveRevisionSnapshot(ProfileConfig profile)
    {
        lock (_sync)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var max = c.CreateCommand();
            max.Transaction = tx;
            max.CommandText = "SELECT COALESCE(MAX(Revision), 0) FROM ProfileRevision WHERE ProfileId = $p;";
            max.Parameters.AddWithValue("$p", profile.Id.ToString("D"));
            var next = Math.Max(Convert.ToInt64(max.ExecuteScalar(), CultureInfo.InvariantCulture), profile.ConfigRevision - 1) + 1;
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO ProfileRevision (ProfileId, Revision, Snapshot, CreatedAt) VALUES ($p, $r, $s, $t);";
            ins.Parameters.AddWithValue("$p", profile.Id.ToString("D"));
            ins.Parameters.AddWithValue("$r", next);
            ins.Parameters.AddWithValue("$s", JsonSerializer.Serialize(profile with { ConfigRevision = next }, SnapshotJson));
            ins.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
            ins.ExecuteNonQuery();
            tx.Commit();
            return next;
        }
    }

    public ProfileConfig? GetRevisionSnapshot(Guid profileId, long revision)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Snapshot FROM ProfileRevision WHERE ProfileId = $p AND Revision = $r;";
            cmd.Parameters.AddWithValue("$p", profileId.ToString("D"));
            cmd.Parameters.AddWithValue("$r", revision);
            var s = cmd.ExecuteScalar() as string;
            return s is null ? null : JsonSerializer.Deserialize<ProfileConfig>(s, SnapshotJson);
        }
    }

    private static readonly string[] Columns =
    [
        "Id", "DisplayName", "Kind", "TestStartUrl", "GraphicsPolicy", "EmailLabel", "Color", "SortOrder", "IsFavorite", "IsPinned", "ConfigRevision", "LastAppliedRevision",
        "PendingRevision", "NetworkMode", "WebRtcPagePolicy", "WebRtcNetworkPolicy", "ProxyHost", "ProxyPort", "ProxyType", "ProxyAuthMode", "ProxyCredentialRef", "ProxyConfigured",
        "UserAgentMode", "CustomUserAgent", "LanguageMode", "LanguageTag", "ScriptLocaleMode", "ScriptLocaleTag", "BrowserTimeZoneId", "ColorScheme",
        "ZoomFactor", "WindowBounds", "TrackingPreventionLevel", "DownloadDirectory", "LastOpenedAt", "LastUserConfirmedVisitAt",
        "ConfirmationLocalDate", "ConfirmationTimeZoneId", "ReminderMonths", "SnoozedUntil",
    ];

    private static void Bind(SqliteCommand cmd, ProfileConfig p)
    {
        object N(object? v) => v ?? DBNull.Value;
        var ep = p.Proxy?.Endpoint;
        cmd.Parameters.AddWithValue("$Id", p.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$DisplayName", p.DisplayName.Trim());
        cmd.Parameters.AddWithValue("$Kind", (int)p.Kind);
        cmd.Parameters.AddWithValue("$TestStartUrl", N(p.TestStartUrl));
        cmd.Parameters.AddWithValue("$GraphicsPolicy", (int)p.GraphicsPolicy);
        cmd.Parameters.AddWithValue("$EmailLabel", N(p.EmailLabel));
        cmd.Parameters.AddWithValue("$Color", p.Color);
        cmd.Parameters.AddWithValue("$SortOrder", p.SortOrder);
        cmd.Parameters.AddWithValue("$IsFavorite", p.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$IsPinned", p.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$ConfigRevision", p.ConfigRevision);
        cmd.Parameters.AddWithValue("$LastAppliedRevision", N(p.LastAppliedRevision));
        cmd.Parameters.AddWithValue("$PendingRevision", N(p.PendingRevision));
        cmd.Parameters.AddWithValue("$NetworkMode", (int)p.NetworkMode);
        cmd.Parameters.AddWithValue("$WebRtcPagePolicy", (int)p.WebRtcPagePolicy);
        cmd.Parameters.AddWithValue("$WebRtcNetworkPolicy", (int)p.WebRtcNetworkPolicy);
        cmd.Parameters.AddWithValue("$ProxyHost", N(ep?.Host));
        cmd.Parameters.AddWithValue("$ProxyPort", N(ep?.Port));
        cmd.Parameters.AddWithValue("$ProxyType", N(ep is null ? null : (int)ep.Type));
        cmd.Parameters.AddWithValue("$ProxyAuthMode", N(p.Proxy is null ? null : (int)p.Proxy.AuthMode));
        cmd.Parameters.AddWithValue("$ProxyCredentialRef", N(p.Proxy?.CredentialRef));
        cmd.Parameters.AddWithValue("$ProxyConfigured", p.Proxy is null ? 0 : 1);
        cmd.Parameters.AddWithValue("$UserAgentMode", (int)p.UserAgentMode);
        cmd.Parameters.AddWithValue("$CustomUserAgent", N(p.CustomUserAgent));
        cmd.Parameters.AddWithValue("$LanguageMode", (int)p.LanguageMode);
        cmd.Parameters.AddWithValue("$LanguageTag", N(p.LanguageTag));
        cmd.Parameters.AddWithValue("$ScriptLocaleMode", (int)p.ScriptLocaleMode);
        cmd.Parameters.AddWithValue("$ScriptLocaleTag", N(p.ScriptLocaleTag));
        cmd.Parameters.AddWithValue("$BrowserTimeZoneId", N(p.BrowserTimeZoneId));
        cmd.Parameters.AddWithValue("$ColorScheme", (int)p.ColorScheme);
        cmd.Parameters.AddWithValue("$ZoomFactor", p.ZoomFactor);
        cmd.Parameters.AddWithValue("$WindowBounds", N(p.WindowBounds is null ? null : JsonSerializer.Serialize(p.WindowBounds)));
        cmd.Parameters.AddWithValue("$TrackingPreventionLevel", (int)p.TrackingPreventionLevel);
        cmd.Parameters.AddWithValue("$DownloadDirectory", N(p.DownloadDirectory));
        cmd.Parameters.AddWithValue("$LastOpenedAt", N(p.LastOpenedAt is null ? null : Iso(p.LastOpenedAt.Value)));
        cmd.Parameters.AddWithValue("$LastUserConfirmedVisitAt", N(p.LastUserConfirmedVisitAt is null ? null : Iso(p.LastUserConfirmedVisitAt.Value)));
        cmd.Parameters.AddWithValue("$ConfirmationLocalDate", N(p.ConfirmationLocalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        cmd.Parameters.AddWithValue("$ConfirmationTimeZoneId", N(p.ConfirmationTimeZoneId));
        cmd.Parameters.AddWithValue("$ReminderMonths", p.ReminderMonths);
        cmd.Parameters.AddWithValue("$SnoozedUntil", N(p.SnoozedUntil is null ? null : Iso(p.SnoozedUntil.Value)));
    }

    private static ProfileConfig ReadProfile(SqliteDataReader r)
    {
        string? S(string n) => r.IsDBNull(r.GetOrdinal(n)) ? null : r.GetString(r.GetOrdinal(n));
        long? L(string n) => r.IsDBNull(r.GetOrdinal(n)) ? null : r.GetInt64(r.GetOrdinal(n));
        int I(string n) => r.GetInt32(r.GetOrdinal(n));

        ProxySettings? proxy = null;
        if (I("ProxyConfigured") == 1)
        {
            ProxyEndpoint? ep = null;
            if (S("ProxyHost") is { } host && L("ProxyPort") is { } port && ProxyEndpoint.TryCreate("http", host, (int)port, out var parsed, out _))
                ep = parsed;
            proxy = new ProxySettings(ep, (ProxyAuthMode)(L("ProxyAuthMode") ?? 0), S("ProxyCredentialRef"));
        }

        var wb = S("WindowBounds");
        return new ProfileConfig
        {
            Id = Guid.Parse(S("Id")!),
            DisplayName = S("DisplayName")!,
            Kind = (ProfileKind)I("Kind"),
            TestStartUrl = S("TestStartUrl"),
            GraphicsPolicy = (GraphicsPolicy)I("GraphicsPolicy"),
            EmailLabel = S("EmailLabel"),
            Color = S("Color")!,
            SortOrder = I("SortOrder"),
            IsFavorite = I("IsFavorite") == 1,
            IsPinned = I("IsPinned") == 1,
            ConfigRevision = L("ConfigRevision") ?? 1,
            LastAppliedRevision = L("LastAppliedRevision"),
            PendingRevision = L("PendingRevision"),
            NetworkMode = (NetworkMode)I("NetworkMode"),
            WebRtcPagePolicy = (WebRtcPagePolicy)I("WebRtcPagePolicy"),
            WebRtcNetworkPolicy = (WebRtcNetworkPolicy)I("WebRtcNetworkPolicy"),
            Proxy = proxy,
            UserAgentMode = (UserAgentMode)I("UserAgentMode"),
            CustomUserAgent = S("CustomUserAgent"),
            LanguageMode = (LanguageMode)I("LanguageMode"),
            LanguageTag = S("LanguageTag"),
            ScriptLocaleMode = (ScriptLocaleMode)I("ScriptLocaleMode"),
            ScriptLocaleTag = S("ScriptLocaleTag"),
            BrowserTimeZoneId = S("BrowserTimeZoneId"),
            ColorScheme = (ColorSchemePreference)I("ColorScheme"),
            ZoomFactor = r.GetDouble(r.GetOrdinal("ZoomFactor")),
            WindowBounds = wb is null ? null : JsonSerializer.Deserialize<WindowBounds>(wb),
            TrackingPreventionLevel = (TrackingPreventionLevel)I("TrackingPreventionLevel"),
            DownloadDirectory = S("DownloadDirectory"),
            LastOpenedAt = ParseTime(S("LastOpenedAt")),
            LastUserConfirmedVisitAt = ParseTime(S("LastUserConfirmedVisitAt")),
            ConfirmationLocalDate = S("ConfirmationLocalDate") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            ConfirmationTimeZoneId = S("ConfirmationTimeZoneId"),
            ReminderMonths = I("ReminderMonths"),
            SnoozedUntil = ParseTime(S("SnoozedUntil")),
        };
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_sync)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string? s) =>
        s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
