using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public sealed class WebViewDefaultProfileMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allmail-profile-migration-" + Guid.NewGuid().ToString("N"));
    private string BrowserRoot => Path.Combine(_root, "EBWebView");
    private void Write(string folder, string text)
    {
        Directory.CreateDirectory(Path.Combine(BrowserRoot, folder, "Local Storage"));
        File.WriteAllText(Path.Combine(BrowserRoot, folder, "Local Storage", "fixture"), text);
    }
    private string Read(string folder) => File.ReadAllText(Path.Combine(BrowserRoot, folder, "Local Storage", "fixture"));

    [Fact]
    public void Existing_session_and_previous_default_are_preserved_and_migration_does_not_repeat()
    {
        Write(WebViewDefaultProfileMigration.LegacyDirectory, "mail session");
        Write("Default", "diagnostics session");
        WebViewDefaultProfileMigration.Prepare(_root);
        Assert.Equal("mail session", Read("Default"));
        Assert.Equal("mail session", Read(WebViewDefaultProfileMigration.BackupDirectory));
        Assert.Equal("diagnostics session", Read(WebViewDefaultProfileMigration.PreviousDefaultDirectory));
        Write("Default", "new login");
        WebViewDefaultProfileMigration.Prepare(_root);
        Assert.Equal("new login", Read("Default"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_swap_is_recovered_before_retry(bool legacyAlreadyMoved)
    {
        Write(WebViewDefaultProfileMigration.LegacyDirectory, "mail session");
        Write(WebViewDefaultProfileMigration.PreviousDefaultDirectory, "diagnostics session");
        if (legacyAlreadyMoved) Directory.Move(Path.Combine(BrowserRoot, WebViewDefaultProfileMigration.LegacyDirectory), Path.Combine(BrowserRoot, "Default"));
        File.WriteAllText(Path.Combine(BrowserRoot, "AllMails-v39-default.pending"), "pending");
        WebViewDefaultProfileMigration.Prepare(_root);
        Assert.Equal("mail session", Read("Default"));
        Assert.Equal("mail session", Read(WebViewDefaultProfileMigration.BackupDirectory));
        Assert.Equal("diagnostics session", Read(WebViewDefaultProfileMigration.PreviousDefaultDirectory));
    }

    [Fact]
    public void New_profiles_do_not_migrate_or_touch_other_data()
    {
        Write("Default", "already default");
        WebViewDefaultProfileMigration.Prepare(_root);
        Assert.Equal("already default", Read("Default"));
        Assert.False(Directory.Exists(Path.Combine(BrowserRoot, WebViewDefaultProfileMigration.BackupDirectory)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
