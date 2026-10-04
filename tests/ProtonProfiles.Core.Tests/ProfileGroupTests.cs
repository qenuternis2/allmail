using Microsoft.Data.Sqlite;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;

namespace ProtonProfiles.Core.Tests;

public class ProfileGroupTests
{
    [Fact]
    public void Empty_groups_and_memberships_persist_across_reopening_database()
    {
        using var env=new TestEnv();var group=env.Repository.CreateGroup("  Работа  ");var empty=env.Repository.CreateGroup("Личные");
        Assert.True(env.Catalog.Create("Почта",null,"#2563EB",out var p,groupId:group.Id).Saved);
        var reopened=new SqliteProfileRepository(env.Paths.DatabasePath,env.Paths.BackupsRoot);
        Assert.Equal("Работа",reopened.ListGroups()[0].Name);Assert.Equal(empty,reopened.ListGroups()[1]);
        Assert.Equal(group.Id,reopened.ListGroupAssignments()[p!.Id]);
    }
    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("A\nB")] [InlineData("A\0B")]
    public void Invalid_group_names_never_create_metadata(string name)
    {
        using var env=new TestEnv();Assert.Throws<ArgumentException>(()=>env.Repository.CreateGroup(name));Assert.Empty(env.Repository.ListGroups());
    }
    [Fact]
    public void Names_have_bounded_length_and_case_insensitive_unicode_uniqueness()
    {
        using var env=new TestEnv();env.Repository.CreateGroup(new string('A',80));
        Assert.Throws<ArgumentException>(()=>env.Repository.CreateGroup(new string('B',81)));
        var a=env.Repository.CreateGroup("Работа");var b=env.Repository.CreateGroup("Другие");
        Assert.Throws<ArgumentException>(()=>env.Repository.CreateGroup(" работа "));
        Assert.Throws<ArgumentException>(()=>env.Repository.RenameGroup(b.Id,"РАБОТА"));
        env.Repository.RenameGroup(a.Id,"Новое");Assert.Equal("Новое",env.Repository.ListGroups().Single(g=>g.Id==a.Id).Name);
        env.Repository.CreateGroup("é");Assert.Throws<ArgumentException>(()=>env.Repository.CreateGroup("e\u0301"));
    }
    [Fact]
    public async Task Moving_and_deleting_groups_preserves_live_profiles_revisions_and_browser_data()
    {
        using var env=new TestEnv();var p=env.AddProfile();var a=env.Repository.CreateGroup("A");var b=env.Repository.CreateGroup("B");
        Directory.CreateDirectory(env.Paths.UserDataFolder(p.Id));var file=Path.Combine(env.Paths.UserDataFolder(p.Id),"session");File.WriteAllText(file,"keep");
        var lifecycle=env.Lifecycle();await lifecycle.OpenAsync(p.Id);var live=env.Repository.Get(p.Id)!;
        env.Repository.AssignGroup([p.Id],a.Id);env.Repository.AssignGroup([p.Id],b.Id);env.Repository.RenameGroup(b.Id,"C");
        Assert.Equal(live,env.Repository.Get(p.Id));Assert.Equal(b.Id,env.Repository.ListGroupAssignments()[p.Id]);
        env.Repository.DeleteGroup(b.Id);
        Assert.Empty(env.Repository.ListGroupAssignments());Assert.Equal(live,env.Repository.Get(p.Id));Assert.Equal("keep",File.ReadAllText(file));
        Assert.Equal(LifecyclePhase.Open,lifecycle.GetState(p.Id).Phase);
        await lifecycle.CloseAsync(p.Id);
    }
    [Fact]
    public void Assignment_batch_rolls_back_on_unknown_profile_or_group_and_supports_clearing()
    {
        using var env=new TestEnv();var p=env.AddProfile();var a=env.Repository.CreateGroup("A");var b=env.Repository.CreateGroup("B");
        env.Repository.AssignGroup([p.Id],a.Id);
        Assert.Throws<ArgumentException>(()=>env.Repository.AssignGroup([p.Id,Guid.NewGuid()],b.Id));
        Assert.Equal(a.Id,env.Repository.ListGroupAssignments()[p.Id]);
        Assert.Throws<ArgumentException>(()=>env.Repository.AssignGroup([p.Id],Guid.NewGuid()));
        env.Repository.AssignGroup([p.Id,p.Id],null);Assert.Empty(env.Repository.ListGroupAssignments());
    }
    [Fact]
    public void Creation_in_missing_group_does_not_leave_a_partial_profile()
    {
        using var env=new TestEnv();Assert.False(env.Catalog.Create("A",null,"#2563EB",out _,groupId:Guid.NewGuid()).Saved);
        var p=new ProfileConfig {Id=Guid.NewGuid(),DisplayName="B"};
        Assert.Throws<ArgumentException>(()=>env.Repository.InsertInGroup(p,Guid.NewGuid()));Assert.Empty(env.Repository.ListProfiles());
    }
    [Fact]
    public void Profile_delete_only_removes_its_membership_and_retains_the_group()
    {
        using var env=new TestEnv();var p=env.AddProfile();var group=env.Repository.CreateGroup("Keep");env.Repository.AssignGroup([p.Id],group.Id);
        env.Repository.DeleteMetadata(p.Id);Assert.Empty(env.Repository.ListGroupAssignments());Assert.Equal(group,Assert.Single(env.Repository.ListGroups()));
    }
    [Fact]
    public void Filtered_reorder_preserves_hidden_profile_slots_and_memberships()
    {
        using var env=new TestEnv();var a=env.AddProfile("A");var b=env.AddProfile("B");var c=env.AddProfile("C");var d=env.AddProfile("D");
        var group=env.Repository.CreateGroup("Subset");env.Repository.AssignGroup([a.Id,c.Id],group.Id);
        env.Catalog.Reorder([c.Id,a.Id]);Assert.Equal(new[]{c.Id,b.Id,a.Id,d.Id},env.Repository.ListProfiles().Select(p=>p.Id));
        Assert.Equal(2,env.Repository.ListGroupAssignments().Count);
        Assert.Throws<ArgumentException>(()=>env.Catalog.Reorder([a.Id,a.Id]));Assert.Throws<ArgumentException>(()=>env.Catalog.Reorder([Guid.NewGuid()]));
    }
    [Fact]
    public void V4_migration_creates_backup_and_keeps_profile_configuration_and_udf()
    {
        using var env=new TestEnv();var p=env.AddProfile("Keep",p=>p with {BrowserTimeZoneId="Europe/Riga",Kind=ProfileKind.Test,TestStartUrl="https://example.test/"});
        Directory.CreateDirectory(env.Paths.UserDataFolder(p.Id));var file=Path.Combine(env.Paths.UserDataFolder(p.Id),"session");File.WriteAllText(file,"keep");
        using(var c=new SqliteConnection($"Data Source={env.Paths.DatabasePath};Pooling=False")) {
            c.Open();using var cmd=c.CreateCommand();cmd.CommandText="DROP TABLE ProfileGroupMember; DROP TABLE ProfileGroup; ALTER TABLE Profile DROP COLUMN PrivacyExceptions; PRAGMA user_version=4;";cmd.ExecuteNonQuery();
        }
        var migrated=new SqliteProfileRepository(env.Paths.DatabasePath,env.Paths.BackupsRoot);
        Assert.Equal(p,migrated.Get(p.Id));Assert.Empty(migrated.ListGroups());Assert.Empty(migrated.ListGroupAssignments());
        Assert.Equal("keep",File.ReadAllText(file));migrated.CreateGroup("After migration");
        var backup=Assert.Single(Directory.GetFiles(env.Paths.BackupsRoot,"profiles.v4.*.db"));
        using var original=new SqliteConnection($"Data Source={backup};Pooling=False");original.Open();Assert.Equal(4,SqliteProfileRepository.ReadSchemaVersion(original));
    }
}
