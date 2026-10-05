using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Privacy;

public sealed record GeoIpUpdateState
{
    public bool Enabled { get; init; }
    public Guid Revision { get; init; } = Guid.NewGuid();
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public string? Error { get; init; }
    public Guid? InstalledGeneration { get; init; }
    [JsonIgnore]
    public DateTimeOffset? NextAttemptUtc => LastSuccessUtc is null
        ? LastAttemptUtc?.AddDays(1)
        : Error is not null || LastAttemptUtc > LastSuccessUtc ? LastAttemptUtc?.AddDays(1) : LastSuccessUtc?.AddDays(7);
}

/// <summary>Opt-in, app-lifetime updater. System network only; never borrows profile cookies or credentials.</summary>
public sealed class MailfudGeoIpUpdater : IDisposable
{
    public static readonly Uri Ipv4Url = new("https://mailfud.org/geoip-legacy/GeoIPCity.dat.gz");
    public static readonly Uri Ipv6Url = new("https://mailfud.org/geoip-legacy/GeoIPCityv6.dat.gz");
    private static readonly object StateGate = new();
    private readonly ManagedPaths _paths;
    private readonly GeoIpTimeZoneDatabase _database;
    private readonly Func<HttpClient> _createClient;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _attempt;
    private readonly object _attemptGate = new();
    private string StatePath => Path.Combine(_paths.GeoIpDirectory, "mailfud-update.json");
    public event Action? Changed;
    public bool IsUpdating { get { lock (_attemptGate) return _attempt is not null; } }

    public MailfudGeoIpUpdater(ManagedPaths paths, Func<HttpClient>? createClient = null, TimeProvider? clock = null)
    {
        _paths = paths; _database = new(paths); _createClient = createClient ?? CreateSystemClient;
        _clock = clock ?? TimeProvider.System;
    }

    public static HttpClient CreateSystemClient() => new(new HttpClientHandler {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        AutomaticDecompression = DecompressionMethods.None, UseProxy = true
    }) { Timeout = TimeSpan.FromMinutes(3) };

    public GeoIpUpdateState State
    {
        get
        {
            lock (StateGate)
            {
                try { return ReadState(); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                { return new() { Error = "Настройки обновления повреждены или недоступны; автообновление выключено." }; }
            }
        }
    }

    private GeoIpUpdateState ReadState()
    {
        if (!File.Exists(StatePath)) return new();
        if (new FileInfo(StatePath).Length > 8192) throw new InvalidDataException("Invalid updater state");
        var state = JsonSerializer.Deserialize<GeoIpUpdateState>(File.ReadAllText(StatePath));
        if (state is null || state.Revision == Guid.Empty) throw new InvalidDataException("Invalid updater state");
        // Avoid corrupted timestamps breaking the background loop or suppressing updates indefinitely.
        var now = _clock.GetUtcNow();
        if (state.LastSuccessUtc > now.AddDays(1) || state.LastAttemptUtc > now.AddDays(1)
            || state.LastSuccessUtc?.Year > 9990 || state.LastAttemptUtc?.Year > 9990)
            throw new InvalidDataException("Invalid updater dates");
        return state;
    }

    private void WriteState(GeoIpUpdateState state)
    {
        Directory.CreateDirectory(_paths.GeoIpDirectory);
        var temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, state); stream.Flush(flushToDisk: true); }
            File.Move(temp, StatePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Global preference, applied immediately, independently of the profile editor's Save/Cancel.</summary>
    public void Configure(bool enabled)
    {
        lock (StateGate) WriteState(State with { Enabled = enabled, Revision = Guid.NewGuid() });
        lock (_attemptGate) _attempt?.Cancel();
        Changed?.Invoke();
    }

    public async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await UpdateAsync();
                await Task.Delay(TimeSpan.FromMinutes(15), _clock, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    /// <param name="force">Manual download also works with automatic updates disabled.</param>
    public async Task<bool> UpdateAsync(bool force = false)
    {
        CancellationTokenSource attempt;
        lock (_attemptGate)
        {
            if (_attempt is not null || _lifetime.IsCancellationRequested) return false;
            attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            attempt.CancelAfter(TimeSpan.FromMinutes(8)); _attempt = attempt;
        }
        string? staging = null;
        FileStream? lease = null;
        GeoIpUpdateState? started = null;
        var activated = false;
        try
        {
            var state = State;
            if (!force && (!state.Enabled || state.NextAttemptUtc > _clock.GetUtcNow())) return false;
            Directory.CreateDirectory(_paths.GeoIpDirectory);
            // Only one instance downloads at a time. An abandoned OS handle is released on crash.
            try { lease = new FileStream(Path.Combine(_paths.GeoIpDirectory, "mailfud-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return false; }
            lock (StateGate)
            {
                state = ReadState(); // Recheck after acquiring the lease: another instance may have updated it.
                if (!force && (!state.Enabled || state.NextAttemptUtc > _clock.GetUtcNow())) return false;
                started = state with { LastAttemptUtc = _clock.GetUtcNow(), Error = null };
                WriteState(started);
            }
            Changed?.Invoke();
            var previousFiles = _database.InstalledFiles.ToArray();
            staging = Path.Combine(_paths.GeoIpDirectory, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var ipv4 = Path.Combine(staging, "GeoIPCity.dat.gz"); var ipv6 = Path.Combine(staging, "GeoIPCityv6.dat.gz");
            using var client = _createClient();
            await DownloadAsync(client, Ipv4Url, ipv4, attempt.Token);
            await DownloadAsync(client, Ipv6Url, ipv6, attempt.Token);
            // Validate both files in an isolated DB before making any change to the active database.
            var validationPaths = new ManagedPaths(Path.Combine(staging, "validation"));
            var validation = new GeoIpTimeZoneDatabase(validationPaths);
            await Task.Run(() => validation.InstallUpdate([ipv4, ipv6], () => !attempt.IsCancellationRequested, attempt.Token), attempt.Token);
            var checkedFiles = validation.InstalledFiles;
            using (var v4 = new LegacyGeoIpCityReader(checkedFiles.Single(f => f.EndsWith("GeoIPCity.dat", StringComparison.Ordinal))))
            using (var v6 = new LegacyGeoIpCityReader(checkedFiles.Single(f => f.EndsWith("GeoIPCityv6.dat", StringComparison.Ordinal))))
            {
                if (v4.Info.BuildDateSource != "database" || v6.Info.BuildDateSource != "database" || v4.Info.BuildDate != v6.Info.BuildDate)
                    throw new InvalidDataException("Несогласованные даты Mailfud IPv4/IPv6.");
                GeoIpDatabaseInfo? previousInfo;
                try { previousInfo = _database.Inspect(); }
                catch (Exception e) when (e is IOException or InvalidDataException or MaxMind.Db.InvalidDatabaseException) { previousInfo = null; }
                if (previousInfo is not null && v4.Info.BuildDate < previousInfo.BuildDate)
                    throw new InvalidDataException("Mailfud предлагает более старую базу.");
            }
            var installed = await Task.Run(() => _database.InstallUpdate([ipv4, ipv6], () => {
                lock (StateGate) return ReadState().Revision == started.Revision
                    && previousFiles.SequenceEqual(_database.InstalledFiles, StringComparer.Ordinal);
            }, attempt.Token), attempt.Token);
            activated = true;
            lock (StateGate)
            {
                var current = ReadState();
                if (current.Revision == started.Revision) WriteState(current with { LastSuccessUtc = _clock.GetUtcNow(), Error = null, InstalledGeneration = installed.Generation });
            }
            if (started.InstalledGeneration is { } old && old != installed.Generation)
                try { _database.RemoveObsoleteUpdate(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            if (started is not null && !_lifetime.IsCancellationRequested)
            {
                lock (StateGate)
                {
                    try
                    {
                        var current = ReadState();
                        if (current.Revision == started.Revision) WriteState(current with {
                            Error = activated ? "База обновлена, но не удалось сохранить расписание."
                                : e is OperationCanceledException ? "Обновление отменено или превышено время ожидания."
                                : "Не удалось загрузить и проверить обе базы Mailfud. Прежняя база сохранена."
                        });
                    }
                    catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
                }
            }
            return false;
        }
        finally
        {
            if (staging is not null) try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            lease?.Dispose();
            lock (_attemptGate) { _attempt = null; attempt.Dispose(); }
            Changed?.Invoke();
        }
    }

    private static async Task DownloadAsync(HttpClient client, Uri uri, string path, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead does not apply HttpClient.Timeout to the response body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3)); cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != uri)
            throw new HttpRequestException("Mailfud: invalid response or redirect");
        if (response.Content.Headers.ContentLength is <= 0 or > GeoIpTimeZoneDatabase.MaximumBytes)
            throw new InvalidDataException("Mailfud: invalid download size");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += count;
            if (total > GeoIpTimeZoneDatabase.MaximumBytes) throw new InvalidDataException("Mailfud: download exceeds 256 MB");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        if (total == 0) throw new InvalidDataException("Mailfud: empty download");
        await output.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        lock (_attemptGate) _attempt?.Cancel();
        // CTS lives until the background task exits; Dispose is also safe during a download.
    }
}
