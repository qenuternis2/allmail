using System.IO.Compression;
using System.Net;
using System.Text;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class MailfudGeoIpUpdaterTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-city", name);
    private static byte[] Gzip(string name, string date = "20261009")
    {
        var bytes = File.ReadAllBytes(Fixture(name));
        var source = Encoding.ASCII.GetBytes("20261002"); var replacement = Encoding.ASCII.GetBytes(date);
        var offset = bytes.AsSpan().LastIndexOf(source); Assert.True(offset >= 0); replacement.CopyTo(bytes, offset);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
    private static HttpResponseMessage Good(HttpRequestMessage request) => new(HttpStatusCode.OK) {
        RequestMessage = request,
        Content = new ByteArrayContent(Gzip(request.RequestUri == MailfudGeoIpUpdater.Ipv4Url ? "GeoIPCity.dat" : "GeoIPCityv6.dat"))
    };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));

    [Fact]
    public async Task Default_is_off_and_enabling_installs_both_families_then_waits_seven_days_across_restart()
    {
        using var env = new TestEnv(); var clock = new Clock(); var requests = new List<Uri>();
        Func<HttpClient> create = () => Client((r, _) => {
            Assert.Null(r.Headers.Authorization); Assert.Null(r.Headers.Referrer); Assert.Empty(r.Headers.UserAgent);
            Assert.False(r.Headers.Contains("Cookie")); Assert.True(r.Headers.CacheControl?.NoCache);
            requests.Add(r.RequestUri!); return Task.FromResult(Good(r));
        });
        using (var updater = new MailfudGeoIpUpdater(env.Paths, create, clock))
        {
            Assert.False(await updater.UpdateAsync()); Assert.Empty(requests);
            updater.Configure(true); Assert.True(await updater.UpdateAsync());
            Assert.Equal([MailfudGeoIpUpdater.Ipv4Url, MailfudGeoIpUpdater.Ipv6Url], requests);
            Assert.Equal(clock.Now.AddDays(7), updater.State.NextAttemptUtc);
            var db = new GeoIpTimeZoneDatabase(env.Paths);
            Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
            Assert.Equal("Asia/Tokyo", db.Resolve([IPAddress.Parse("2001:218::")]).TimeZoneId);
            clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1);
            Assert.False(await updater.UpdateAsync()); Assert.Equal(2, requests.Count);
        }
        using var resumed = new MailfudGeoIpUpdater(env.Paths, create, clock);
        Assert.True(resumed.State.Enabled); Assert.False(await resumed.UpdateAsync());
        clock.Now += TimeSpan.FromSeconds(1); Assert.True(await resumed.UpdateAsync()); Assert.Equal(4, requests.Count);
        clock.Now += TimeSpan.FromDays(30); Assert.True(await resumed.UpdateAsync()); // Closed app does not lose an overdue update.
        Assert.Equal(6, requests.Count);
        Assert.Single(Directory.GetDirectories(env.Paths.GeoIpDirectory, "db-*")); // Weekly downloads do not accumulate generations.
        Assert.Empty(Directory.GetDirectories(env.Paths.GeoIpDirectory, "download-*"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("redirect")]
    [InlineData("redirect-target")]
    [InlineData("empty")]
    [InlineData("size")]
    [InlineData("gzip")]
    [InlineData("dates")]
    [InlineData("no-date")]
    [InlineData("duplicate-family")]
    [InlineData("timeout")]
    public async Task Failed_second_file_preserves_active_pair_and_backs_off_for_a_day(string failure)
    {
        using var env = new TestEnv(); var clock = new Clock(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        db.Install(Fixture("GeoIPCity.dat"), Fixture("GeoIPCityv6.dat")); var oldFiles = db.InstalledFiles.ToArray(); var count = 0;
        using var updater = new MailfudGeoIpUpdater(env.Paths, () => Client((r, _) => {
            count++;
            var response = Good(r);
            if (r.RequestUri == MailfudGeoIpUpdater.Ipv6Url)
            {
                switch (failure)
                {
                    case "timeout": throw new TaskCanceledException("Timed out");
                    case "http": response.StatusCode = HttpStatusCode.ServiceUnavailable; break;
                    case "redirect": response.StatusCode = HttpStatusCode.Found; response.Headers.Location = new("https://example.org/"); break;
                    case "redirect-target": response.RequestMessage = new(HttpMethod.Get, "https://example.org/"); break;
                    case "empty": response.Content = new ByteArrayContent([]); break;
                    case "size": response.Content.Headers.ContentLength = GeoIpTimeZoneDatabase.MaximumBytes + 1; break;
                    case "gzip": response.Content = new ByteArrayContent([1, 2, 3, 4]); break;
                    case "dates": response.Content = new ByteArrayContent(Gzip("GeoIPCityv6.dat", "20261002")); break;
                    case "no-date": response.Content = new ByteArrayContent(Gzip("GeoIPCityv6.dat", "00000000")); break;
                    case "duplicate-family": response.Content = new ByteArrayContent(Gzip("GeoIPCity.dat")); break;
                }
            }
            return Task.FromResult(response);
        }), clock);
        updater.Configure(true); Assert.False(await updater.UpdateAsync()); Assert.Equal(2, count);
        Assert.Equal(oldFiles, db.InstalledFiles); Assert.Null(updater.State.LastSuccessUtc); Assert.NotNull(updater.State.Error);
        Assert.Equal(clock.Now.AddDays(1), updater.State.NextAttemptUtc);
        clock.Now += TimeSpan.FromHours(23); Assert.False(await updater.UpdateAsync()); Assert.Equal(2, count);
        clock.Now += TimeSpan.FromHours(1); Assert.False(await updater.UpdateAsync()); Assert.Equal(4, count);
        Assert.Equal("Europe/London", db.Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
        Assert.Empty(Directory.GetDirectories(env.Paths.GeoIpDirectory, "download-*"));
        Assert.Single(Directory.GetDirectories(env.Paths.GeoIpDirectory, "db-*"));
    }

    [Fact]
    public async Task Valid_pair_can_repair_a_damaged_old_mmdb()
    {
        using var env = new TestEnv(); Directory.CreateDirectory(env.Paths.GeoIpDirectory);
        File.WriteAllText(env.Paths.GeoIpDatabasePath, "corrupt mmdb");
        using var updater = new MailfudGeoIpUpdater(env.Paths, () => Client((r, _) => Task.FromResult(Good(r))));
        Assert.True(await updater.UpdateAsync(force: true));
        Assert.Equal("Europe/London", new GeoIpTimeZoneDatabase(env.Paths).Resolve([IPAddress.Parse("81.2.69.160")]).TimeZoneId);
    }

    [Fact]
    public async Task Older_download_is_rejected_and_manual_imports_are_not_garbage_collected()
    {
        using var env = new TestEnv(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        db.Install(Fixture("GeoIPCity.dat")); var manualFiles = db.InstalledFiles.ToArray();
        using var updater = new MailfudGeoIpUpdater(env.Paths, () => Client((r, _) => Task.FromResult(Good(r))));
        Assert.True(await updater.UpdateAsync(force: true)); Assert.True(await updater.UpdateAsync(force: true));
        Assert.All(manualFiles, file => Assert.True(File.Exists(file)));
        Assert.Equal(2, Directory.GetDirectories(env.Paths.GeoIpDirectory, "db-*").Length);
        var newer = Path.Combine(env.Root, "newer.dat.gz"); File.WriteAllBytes(newer, Gzip("GeoIPCity.dat", "20261010"));
        db.Install(newer); var active = db.InstalledFiles.ToArray();
        Assert.False(await updater.UpdateAsync(force: true)); Assert.Equal(active, db.InstalledFiles);
    }

    [Theory]
    [InlineData("{\"Enabled\":true,\"Revision\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"Enabled\":true,\"Revision\":\"67cdb50e-5ae7-4631-9d36-4f2b9436da00\",\"LastSuccessUtc\":\"9999-12-31T23:59:59Z\"}")]
    public async Task Invalid_scheduler_state_does_not_trigger_network_requests(string json)
    {
        using var env = new TestEnv(); Directory.CreateDirectory(env.Paths.GeoIpDirectory);
        File.WriteAllText(Path.Combine(env.Paths.GeoIpDirectory, "mailfud-update.json"), json);
        using var updater = new MailfudGeoIpUpdater(env.Paths, () => throw new InvalidOperationException("Must not download"));
        Assert.False(updater.State.Enabled); Assert.NotNull(updater.State.Error); Assert.False(await updater.UpdateAsync());
        Assert.False(await updater.UpdateAsync(force: true));
    }

    [Fact]
    public async Task Manual_download_works_while_disabled_and_success_replaces_failure_backoff()
    {
        using var env = new TestEnv(); var clock = new Clock(); var bad = true; var count = 0;
        using var updater = new MailfudGeoIpUpdater(env.Paths, () => Client((r, _) => {
            count++; return Task.FromResult(bad ? new HttpResponseMessage(HttpStatusCode.BadGateway) { RequestMessage = r } : Good(r));
        }), clock);
        updater.Configure(true); Assert.False(await updater.UpdateAsync()); Assert.Equal(1, count);
        bad = false; Assert.True(await updater.UpdateAsync(force: true)); Assert.Null(updater.State.Error);
        Assert.Equal(clock.Now.AddDays(7), updater.State.NextAttemptUtc);
        updater.Configure(false); Assert.True(await updater.UpdateAsync(force: true)); Assert.False(updater.State.Enabled);
        Assert.False(await updater.UpdateAsync()); Assert.Equal(5, count);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("manual-import")]
    [InlineData("another-instance-disables")]
    [InlineData("shutdown")]
    public async Task Changes_during_download_never_activate_a_late_pair(string change)
    {
        using var env = new TestEnv(); var db = new GeoIpTimeZoneDatabase(env.Paths);
        db.Install(Fixture("GeoIPCity.dat")); var oldFiles = db.InstalledFiles.ToArray();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var updater = new MailfudGeoIpUpdater(env.Paths, () => Client(async (r, token) => {
            if (r.RequestUri == MailfudGeoIpUpdater.Ipv6Url) { reached.TrySetResult(); await release.Task.WaitAsync(token); }
            return Good(r);
        }));
        updater.Configure(true); var updating = updater.UpdateAsync(); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        switch (change)
        {
            case "disable": updater.Configure(false); break;
            case "manual-import": db.Install(Fixture("GeoIPCityv6.dat")); oldFiles = db.InstalledFiles.ToArray(); break;
            case "another-instance-disables": using (var other = new MailfudGeoIpUpdater(env.Paths)) other.Configure(false); break;
            case "shutdown": updater.Dispose(); break;
        }
        release.TrySetResult(); Assert.False(await updating.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(oldFiles, db.InstalledFiles); Assert.Null(updater.State.LastSuccessUtc);
    }

    [Fact]
    public async Task Only_one_instance_downloads_and_corrupt_settings_fail_closed_until_reconfigured()
    {
        using var env = new TestEnv(); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        Func<HttpClient> create = () => Client(async (r, token) => {
            count++; reached.TrySetResult(); await release.Task.WaitAsync(token); return Good(r);
        });
        using var first = new MailfudGeoIpUpdater(env.Paths, create); using var second = new MailfudGeoIpUpdater(env.Paths, create);
        first.Configure(true); var task = first.UpdateAsync(); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await first.UpdateAsync(force: true)); Assert.False(await second.UpdateAsync(force: true)); Assert.Equal(1, count);
        release.TrySetResult(); Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(2, count);
        File.WriteAllText(Path.Combine(env.Paths.GeoIpDirectory, "mailfud-update.json"), "invalid");
        Assert.False(second.State.Enabled); Assert.NotNull(second.State.Error);
        Assert.False(await second.UpdateAsync()); Assert.False(await second.UpdateAsync(force: true)); Assert.Equal(2, count);
        second.Configure(true); Assert.True(await second.UpdateAsync()); Assert.Equal(4, count);
    }
}
