using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Persistence;

public sealed partial class SqliteProfileRepository
{
    public IReadOnlyList<ProfileGroup> ListGroups()
    {
        lock (_sync) {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id,Name FROM ProfileGroup ORDER BY SortOrder,Name;";
            using var r = cmd.ExecuteReader(); var groups = new List<ProfileGroup>();
            while (r.Read()) groups.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1)));
            return groups;
        }
    }
    public IReadOnlyDictionary<Guid, Guid> ListGroupAssignments()
    {
        lock (_sync) {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT ProfileId,GroupId FROM ProfileGroupMember;";
            using var r = cmd.ExecuteReader(); var members = new Dictionary<Guid, Guid>();
            while (r.Read()) members.Add(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)));
            return members;
        }
    }
    public ProfileGroup CreateGroup(string name)
    {
        var normalized = ProfileGroup.NormalizeName(name); var group = new ProfileGroup(Guid.NewGuid(), normalized);
        lock (_sync) {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO ProfileGroup (Id,Name,NameKey,SortOrder) VALUES ($id,$name,$key,(SELECT COALESCE(MAX(SortOrder),-1)+1 FROM ProfileGroup));";
            cmd.Parameters.AddWithValue("$id", group.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$name", normalized); cmd.Parameters.AddWithValue("$key", normalized.ToUpperInvariant());
            try { cmd.ExecuteNonQuery(); }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new ArgumentException("Группа с таким названием уже существует.", e); }
            return group;
        }
    }
    public void RenameGroup(Guid id, string name)
    {
        var normalized = ProfileGroup.NormalizeName(name);
        lock (_sync) {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE ProfileGroup SET Name=$name,NameKey=$key WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D")); cmd.Parameters.AddWithValue("$name", normalized);
            cmd.Parameters.AddWithValue("$key", normalized.ToUpperInvariant());
            try { if (cmd.ExecuteNonQuery() != 1) throw new ArgumentException("Группа не найдена."); }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new ArgumentException("Группа с таким названием уже существует.", e); }
        }
    }
    public void DeleteGroup(Guid id)
    {
        lock (_sync) {
            using var c = Open(); using var cmd = c.CreateCommand();
            // Only memberships cascade. Profiles, revisions and browser data are retained.
            cmd.CommandText = "DELETE FROM ProfileGroup WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D")); cmd.ExecuteNonQuery();
        }
    }
    public void AssignGroup(IReadOnlyList<Guid> profileIds, Guid? groupId)
    {
        lock (_sync) {
            using var c = Open(); using var tx = c.BeginTransaction();
            if (groupId is { } group) RequireRow(c, tx, "ProfileGroup", group);
            foreach (var id in profileIds.Distinct()) {
                RequireRow(c, tx, "Profile", id);
                SetGroup(c, tx, id, groupId);
            }
            tx.Commit();
        }
    }
    public void InsertInGroup(ProfileConfig profile, Guid? groupId)
    {
        lock (_sync) {
            using var c = Open(); using var tx = c.BeginTransaction();
            if (groupId is { } group) RequireRow(c, tx, "ProfileGroup", group);
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO Profile ({string.Join(",", Columns)}) VALUES ({string.Join(",", Columns.Select(x => "$" + x))});";
            Bind(cmd, profile); cmd.ExecuteNonQuery();
            if (groupId is not null) SetGroup(c, tx, profile.Id, groupId);
            tx.Commit();
        }
    }
    private static void RequireRow(SqliteConnection c, SqliteTransaction tx, string table, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE Id=$id;"; // table is a fixed internal name.
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        if (cmd.ExecuteScalar() is null) throw new ArgumentException(table == "Profile" ? "Профиль не найден." : "Группа не найдена.");
    }
    private static void SetGroup(SqliteConnection c, SqliteTransaction tx, Guid id, Guid? groupId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = groupId is null ? "DELETE FROM ProfileGroupMember WHERE ProfileId=$id;"
            : "INSERT INTO ProfileGroupMember (ProfileId,GroupId) VALUES ($id,$group) ON CONFLICT(ProfileId) DO UPDATE SET GroupId=$group;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        if (groupId is { } group) cmd.Parameters.AddWithValue("$group", group.ToString("D"));
        cmd.ExecuteNonQuery();
    }
}
