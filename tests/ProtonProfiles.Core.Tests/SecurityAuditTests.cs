using System.Text.Json;
using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

public sealed class SecurityAuditTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Cleanup_rejects_linked_managed_root_and_ancestors(bool linkedAncestor, bool reset)
    {
        using var env = new TestEnv();
        var outside = Path.Combine(env.Root, "outside");
        var alias = Path.Combine(env.Root, "alias");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(alias, outside);
        var paths = new ManagedPaths(linkedAncestor ? Path.Combine(alias, "managed") : alias);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(paths.UserDataFolder(id));
        var marker = Path.Combine(paths.UserDataFolder(id), "external-data.txt");
        File.WriteAllText(marker, "must survive");

        var deleter = new SafeProfileDeleter(paths);
        var result = reset ? deleter.DeleteUserDataFolder(id) : deleter.DeleteProfileDirectory(id);

        Assert.Equal(CleanupOutcome.RejectedUnsafePath, result.Outcome);
        Assert.NotNull(deleter.ValidateOwnership(id, paths.UserDataFolder(id)));
        Assert.Equal("must survive", File.ReadAllText(marker));
    }

    [Theory]
    [InlineData("+1+2")]
    [InlineData("-1+2")]
    [InlineData("=1+2")]
    [InlineData("@SUM(1)")]
    [InlineData("  +1+2")]
    [InlineData("\t=1+2")]
    public void Network_log_exports_untrusted_cells_as_spreadsheet_text(string method)
    {
        // +1+2 is a valid HTTP token: a website can issue it as a custom fetch method.
        var log = new ConnectionLog();
        var parser = new CdpNetworkParser(log.Add);
        parser.Handle("Network.requestWillBeSent", JsonSerializer.Serialize(new
        {
            requestId = "fixture", request = new { url = "https://fixture.invalid/", method },
            wallTime = 1_700_000_000, timestamp = 1.0, type = "Fetch"
        }));
        parser.Handle("Network.loadingFinished", "{\"requestId\":\"fixture\",\"timestamp\":2,\"encodedDataLength\":0}");

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(method, entry.Method); // Raw diagnostics and JSON keep the original value.
        using var exported = JsonDocument.Parse(log.ExportJson());
        Assert.Equal(method, exported.RootElement[0].GetProperty("Method").GetString());
        var cells = ConnectionLog.ToTsv(entry).Split('\t');
        Assert.Equal(ConnectionLog.TsvColumns.Length, cells.Length);
        Assert.StartsWith("'", cells[Array.IndexOf(ConnectionLog.TsvColumns, "method")]);
    }

    [Fact]
    public void Ordinary_network_log_cells_keep_their_values()
    {
        var log = new ConnectionLog();
        var parser = new CdpNetworkParser(log.Add);
        parser.Handle("Network.requestWillBeSent", "{\"requestId\":\"fixture\",\"request\":{\"url\":\"https://fixture.invalid/\",\"method\":\"GET\"},\"timestamp\":1}");
        parser.Handle("Network.loadingFinished", "{\"requestId\":\"fixture\",\"timestamp\":2}");
        var cells = ConnectionLog.ToTsv(Assert.Single(log.Snapshot())).Split('\t');
        Assert.Equal("GET", cells[Array.IndexOf(ConnectionLog.TsvColumns, "method")]);
        Assert.Equal("https://fixture.invalid/", cells[Array.IndexOf(ConnectionLog.TsvColumns, "url")]);
    }

    [Fact]
    public void Credential_string_representation_never_contains_credentials()
    {
        var credential = new ProxyCredential("fixture-private-user", "fixture-private-password");
        var formatted = $"Credential: {credential}";
        Assert.DoesNotContain(credential.Password, formatted);
        Assert.DoesNotContain(credential.UserName, formatted);
        Assert.Equal(new ProxyCredential(credential.UserName, credential.Password), credential);
        Assert.Equal("fixture-private-password", credential.Password);
    }
}
