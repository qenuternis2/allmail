using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public sealed class BrowserTabsStoreTests : IDisposable
{
    private readonly ManagedPaths _paths = new(Path.Combine(Path.GetTempPath(), "allmail-tabs-test-" + Guid.NewGuid().ToString("N")));
    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }

    [Fact]
    public void Roundtrip_preserves_duplicates_blank_tabs_fragments_order_and_active_tab_per_profile()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var store = new BrowserTabsStore(_paths);
        string[] addresses = ["https://example.test/a?q=1#part", "about:blank", "https://example.test/a?q=1#part"];
        store.Save(id, new(addresses, 2));
        store.Save(other, new(["https://other.test/"], 0));
        var read = new BrowserTabsStore(_paths).Load(id)!;
        Assert.Equal(addresses, read.Addresses);
        Assert.Equal(2, read.ActiveIndex);
        Assert.Equal(["https://other.test/"], store.Load(other)!.Addresses);
        Assert.Null(store.Load(Guid.NewGuid()));
        Assert.Empty(Directory.GetFiles(_paths.ProfileDirectory(id), "*.tmp"));
    }

    [Fact]
    public void Closing_all_tabs_replaces_old_session_with_an_empty_list()
    {
        var id = Guid.NewGuid();
        var store = new BrowserTabsStore(_paths);
        store.Save(id, new(["https://example.test/"], 0));
        store.Save(id, new([], 0));
        Assert.Empty(store.Load(id)!.Addresses);
        Assert.Equal(0, store.Load(id)!.ActiveIndex);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":2,\"Addresses\":[\"https://example.test/\"],\"ActiveIndex\":0}")]
    [InlineData("{\"Version\":1,\"Addresses\":null,\"ActiveIndex\":0}")]
    [InlineData("{\"Version\":1,\"Addresses\":[123],\"ActiveIndex\":0}")]
    public void Broken_or_unknown_documents_are_ignored_without_modifying_the_file(string contents)
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_paths.ProfileDirectory(id));
        File.WriteAllText(_paths.TabsStateFile(id), contents);
        Assert.Null(new BrowserTabsStore(_paths).Load(id));
        Assert.Equal(contents, File.ReadAllText(_paths.TabsStateFile(id)));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/test.html")]
    [InlineData("data:text/html,test")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://ua-hints-bootstrap.protonprofiles.invalid/")]
    [InlineData("https://contexts.invalid/")]
    [InlineData("not-a-url")]
    public void Invalid_or_internal_addresses_are_removed_and_active_index_remapped(string invalid)
    {
        var read = BrowserTabsSnapshot.Create(["https://example.test/", invalid, "about:blank", "https://other.test/"], 3);
        Assert.Equal(["https://example.test/", "about:blank", "https://other.test/"], read.Addresses);
        Assert.Equal(2, read.ActiveIndex);
        Assert.Equal(0, BrowserTabsSnapshot.Create(read.Addresses, -1).ActiveIndex);
        Assert.Equal(0, BrowserTabsSnapshot.Create(read.Addresses, 100).ActiveIndex);
    }

    [Fact]
    public void Session_reset_preserves_tabs_and_profile_deletion_removes_them()
    {
        var id = Guid.NewGuid();
        var store = new BrowserTabsStore(_paths);
        store.Save(id, new(["https://example.test/"], 0));
        Directory.CreateDirectory(_paths.UserDataFolder(id));
        File.WriteAllText(Path.Combine(_paths.UserDataFolder(id), "fixture.txt"), "website data");
        var deleter = new SafeProfileDeleter(_paths);
        Assert.True(deleter.DeleteUserDataFolder(id).IsComplete);
        Assert.NotNull(store.Load(id));
        Assert.True(deleter.DeleteProfileDirectory(id).IsComplete);
        Assert.Null(store.Load(id));
    }
}
