using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;

namespace ProtonProfiles.Core.Tests;

/// <summary>Engine double that models the WebView2 lifecycle contract: a retained exit signal per generation.</summary>
public sealed class FakeEngine : IBrowserEngine
{
    public BrowserCapabilities Capabilities { get; set; } = new("Fake", true, ProxySupportLevel.None, true, true, true, true, true, true);
    public List<FakeSession> Sessions { get; } = [];
    public List<BrowserStartRequest> Requests { get; } = [];
    public int StartCalls;
    /// <summary>When set, StartAsync waits for this before returning.</summary>
    public TaskCompletionSource? StartGate { get; set; }
    public bool ExitOnClose { get; set; } = true;
    public Func<BrowserStartRequest, Exception?>? FailWith { get; set; }

    public async Task<IBrowserSession> StartAsync(BrowserStartRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref StartCalls);
        Requests.Add(request);
        if (FailWith?.Invoke(request) is { } ex) throw ex;
        var session = new FakeSession(request.Context, this);
        if (StartGate is not null) await StartGate.Task;
        lock (Sessions) Sessions.Add(session);
        return session;
    }

    public int LiveProcesses() { lock (Sessions) return Sessions.Count(s => !s.ProcessExited.IsCompleted); }
}

public sealed class FakeSession(GenerationContext context, FakeEngine engine) : IBrowserSession
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public GenerationContext Context { get; } = context;
    public Task ProcessExited => _exit.Task;
    public int? BrowserProcessId { get; } = Random.Shared.Next(1000, 60000);
    public string? RuntimeVersion => "fake-1.0";
    public WebRtcReadbackSummary? WebRtcReadback { get; set; }
    public int CloseCalls;

    public Task CloseAsync()
    {
        Interlocked.Increment(ref CloseCalls);
        if (engine.ExitOnClose) _exit.TrySetResult();
        return Task.CompletedTask;
    }

    /// <summary>Simulates the browser process finally exiting (or crashing).</summary>
    public void SignalExit() => _exit.TrySetResult();
}

public sealed class TestEnv : IDisposable
{
    public string Root { get; }
    public ManagedPaths Paths { get; }
    public SqliteProfileRepository Repository { get; }
    public InMemoryCredentialStore Credentials { get; } = new();
    public FakeEngine Engine { get; } = new();
    public ProfileCatalog Catalog { get; }

    public TestEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "pp-tests-" + Guid.NewGuid().ToString("N"));
        Paths = new ManagedPaths(Root);
        Paths.EnsureBaseDirectories();
        Repository = new SqliteProfileRepository(Paths.DatabasePath, Paths.BackupsRoot);
        Catalog = new ProfileCatalog(Repository, Credentials);
    }

    public ProfileLifecycleService Lifecycle(TimeSpan? exitTimeout = null, int max = 3) =>
        new(Repository, Engine, Credentials, Paths, exitTimeout: exitTimeout ?? TimeSpan.FromSeconds(2), maxLiveEnvironments: max);

    public ProfileConfig AddProfile(string name = "A", Func<ProfileConfig, ProfileConfig>? change = null)
    {
        var r = Catalog.Create(name, null, "#2563EB", out var p);
        Assert.True(r.Saved, string.Join(";", r.Errors));
        if (change is not null)
        {
            var changed = change(p!);
            Repository.Update(changed);
            return changed;
        }
        return p!;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
