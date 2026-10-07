using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Privacy;
using ProtonProfiles.Core.Storage;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Lifecycle;

/// <summary>Runtime state, kept separate from persistent configuration (spec §9).</summary>
public sealed record ProfileRuntimeState(
    Guid ProfileId,
    LifecyclePhase Phase,
    long Generation,
    long? ActiveRevision,
    int? BrowserProcessId,
    string? EffectiveRuntimeVersion,
    string? LastError,
    DateTimeOffset? LastActivatedAt)
{
    public WebRtcReadbackSummary? WebRtcReadback { get; init; }
    public PageGuardReadbackSummary? AudioReadback { get; init; }
    public static ProfileRuntimeState Closed(Guid id) => new(id, LifecyclePhase.Closed, 0, null, null, null, null, null);
}

public enum OpenOutcome { Opened, AlreadyOpen, CapacityReached, Blocked, LockedElsewhere, RecoveryRequired, Failed, Cancelled }

public sealed record OpenResult(OpenOutcome Outcome, string? Message = null, CapacityDecision? Capacity = null, NetworkReadiness? Readiness = null);

public enum CloseOutcome { Closed, AlreadyClosed, RecoveryRequired }

public sealed record CloseResult(CloseOutcome Outcome, string? Message = null);

public sealed record CleanupReport(bool Completed, CleanupResult? Files, string? Message);

/// <summary>
/// Serializes open/close/restart/reset/delete per profile (spec §4.6), owns generations, the interprocess lock and
/// the 15-second release wait (spec §4.4). Engine calls stay on the caller's synchronization context; invoke from the
/// WPF UI thread and never block on the returned tasks.
/// </summary>
public sealed class ProfileLifecycleService
{
    public static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(15);

    private readonly IProfileRepository _repository;
    private readonly IBrowserEngine _engine;
    private readonly ICredentialStore _credentials;
    private readonly ManagedPaths _paths;
    private readonly SafeProfileDeleter _deleter;
    private readonly TimeProvider _time;
    private readonly TimeSpan _exitTimeout;
    private readonly int _maxLive;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Slot> _slots = new();
    private long _generationCounter;

    public event Action<ProfileRuntimeState>? StateChanged;

    public ProfileLifecycleService(
        IProfileRepository repository,
        IBrowserEngine engine,
        ICredentialStore credentials,
        ManagedPaths paths,
        TimeProvider? time = null,
        TimeSpan? exitTimeout = null,
        int maxLiveEnvironments = CapacityPolicy.DefaultMaxLiveEnvironments)
    {
        _repository = repository;
        _engine = engine;
        _credentials = credentials;
        _paths = paths;
        _deleter = new SafeProfileDeleter(paths);
        _time = time ?? TimeProvider.System;
        _exitTimeout = exitTimeout ?? DefaultExitTimeout;
        _maxLive = maxLiveEnvironments;
    }

    public BrowserCapabilities Capabilities => _engine.Capabilities;

    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ProfileRuntimeState State;
        public IBrowserSession? Session;
        public ProfileLock? Lock;
        public CancellationTokenSource? StartCts;
        public Task<OpenResult>? PendingOpen;
        public Slot(Guid id) => State = ProfileRuntimeState.Closed(id);
    }

    private Slot GetSlot(Guid id)
    {
        lock (_sync)
        {
            if (!_slots.TryGetValue(id, out var slot)) _slots[id] = slot = new Slot(id);
            return slot;
        }
    }

    public ProfileRuntimeState GetState(Guid id)
    {
        lock (_sync) return _slots.TryGetValue(id, out var s)
            ? s.State with { WebRtcReadback = s.Session?.WebRtcReadback ?? s.State.WebRtcReadback,
                AudioReadback = s.Session?.AudioReadback ?? s.State.AudioReadback }
            : ProfileRuntimeState.Closed(id);
    }

    public IBrowserSession? GetSession(Guid id)
    {
        lock (_sync) return _slots.TryGetValue(id, out var s) ? s.Session : null;
    }

    /// <summary>True only for the live generation of a profile; engines use it to reject stale callbacks (spec §4.3).</summary>
    public bool IsCurrentGeneration(GenerationContext context)
    {
        lock (_sync)
            return _slots.TryGetValue(context.ProfileId, out var s)
                   && s.State.Generation == context.GenerationId
                   && s.State.Phase is LifecyclePhase.Starting or LifecyclePhase.Open;
    }

    public IReadOnlyList<LiveProfileInfo> LiveProfiles()
    {
        lock (_sync)
        {
            return _slots.Values
                .Where(s => s.State.Phase != LifecyclePhase.Closed)
                .Select(s =>
                {
                    var p = _repository.Get(s.State.ProfileId);
                    return new LiveProfileInfo(s.State.ProfileId, p?.DisplayName ?? s.State.ProfileId.ToString(), s.State.Phase, p?.IsPinned ?? false, s.State.LastActivatedAt);
                })
                .ToList();
        }
    }

    public void MarkActivated(Guid id)
    {
        Update(GetSlot(id), s => s with { LastActivatedAt = _time.GetUtcNow() });
    }

    /// <summary>Opens a profile. Duplicate opens coalesce onto the in-flight attempt.</summary>
    public Task<OpenResult> OpenAsync(Guid id)
    {
        var slot = GetSlot(id);
        lock (_sync)
        {
            if (slot.PendingOpen is { IsCompleted: false } pending) return pending;
            var capacity = CapacityPolicy.Check(LiveProfilesUnlocked(), id, _maxLive);
            if (!capacity.Allowed)
                return Task.FromResult(new OpenResult(OpenOutcome.CapacityReached, "Достигнут предел одновременно открытых профилей.", capacity));
            // Reserve capacity immediately so concurrent opens of different profiles cannot exceed the limit.
            if (slot.State.Phase == LifecyclePhase.Closed)
                slot.State = slot.State with { Phase = LifecyclePhase.Starting, LastError = null };
            slot.StartCts?.Dispose();
            slot.StartCts = new CancellationTokenSource();
            var task = OpenCoreAsync(slot, slot.StartCts.Token);
            slot.PendingOpen = task;
            return task;
        }
    }

    private List<LiveProfileInfo> LiveProfilesUnlocked() =>
        _slots.Values.Where(s => s.State.Phase != LifecyclePhase.Closed)
            .Select(s => new LiveProfileInfo(s.State.ProfileId, s.State.ProfileId.ToString(), s.State.Phase, _repository.Get(s.State.ProfileId)?.IsPinned ?? false, s.State.LastActivatedAt))
            .ToList();

    private async Task<OpenResult> OpenCoreAsync(Slot slot, CancellationToken ct)
    {
        await slot.Gate.WaitAsync();
        try
        {
            var id = slot.State.ProfileId;
            switch (slot.State.Phase)
            {
                case LifecyclePhase.Open:
                    return new OpenResult(OpenOutcome.AlreadyOpen);
                case LifecyclePhase.RecoveryRequired:
                    return new OpenResult(OpenOutcome.RecoveryRequired, "Профиль требует восстановления: освобождение ресурсов не подтверждено.");
                case LifecyclePhase.Closing:
                    return new OpenResult(OpenOutcome.Failed, "Профиль закрывается.");
            }
            if (ct.IsCancellationRequested) { SetClosed(slot, null); return new OpenResult(OpenOutcome.Cancelled); }
            return await StartGenerationAsync(slot, id, ct);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Must be called with the gate held. Performs spec §4.5 steps 1–6.</summary>
    private async Task<OpenResult> StartGenerationAsync(Slot slot, Guid id, CancellationToken ct)
    {
        var config = _repository.Get(id);
        if (config is null) { SetClosed(slot, "Профиль не найден."); return new OpenResult(OpenOutcome.Failed, "Профиль не найден."); }
        if (_repository.ListPendingOperations().Any(o => o.ProfileId == id && o.Kind is PendingOperationKind.Delete or PendingOperationKind.Reset))
        {
            SetClosed(slot, "Незавершённая очистка.");
            return new OpenResult(OpenOutcome.Blocked, "Сначала завершите незавершённую очистку профиля.");
        }

        var errors = ProfileValidator.Validate(config);
        if (errors.Count > 0) { SetClosed(slot, errors[0]); return new OpenResult(OpenOutcome.Blocked, string.Join(" ", errors)); }

        var readiness = NetworkReadinessEvaluator.Evaluate(config, _engine.Capabilities, _credentials);
        if (readiness != NetworkReadiness.Ready)
        {
            SetClosed(slot, NetworkReadinessEvaluator.Describe(readiness));
            return new OpenResult(OpenOutcome.Blocked, NetworkReadinessEvaluator.Describe(readiness), Readiness: readiness);
        }

        if (slot.Lock is null)
        {
            if (!ProfileLock.TryAcquire(_paths, id, out var acquired))
            {
                SetClosed(slot, "Профиль открыт в другом экземпляре приложения.");
                return new OpenResult(OpenOutcome.LockedElsewhere, "Профиль уже открыт в другом экземпляре приложения.");
            }
            slot.Lock = acquired;
        }

        var udf = _paths.UserDataFolder(id);
        var ownership = _deleter.ValidateOwnership(id, udf);
        if (ownership is not null) { ReleaseLock(slot); SetClosed(slot, ownership); return new OpenResult(OpenOutcome.Blocked, ownership); }
        Directory.CreateDirectory(udf);

        var generation = Interlocked.Increment(ref _generationCounter);
        var revision = config.ConfigRevision;
        var context = new GenerationContext(id, generation);
        Update(slot, s => s with { Phase = LifecyclePhase.Starting, Generation = generation, ActiveRevision = revision, LastError = null, BrowserProcessId = null, WebRtcReadback = null, AudioReadback = null });

        IBrowserSession session;
        try
        {
            session = await _engine.StartAsync(new BrowserStartRequest(context, config, revision, udf, IsCurrentGeneration), ct);
        }
        catch (BrowserStartException e)
        {
            if (e.ProcessMayExist)
            {
                slot.Session = e.PartialSession;
                Update(slot, s => s with { Phase = LifecyclePhase.RecoveryRequired, LastError = e.Message,
                    EffectiveRuntimeVersion = e.PartialSession?.RuntimeVersion });
                if (e.PartialSession is not null) ObserveRecovery(slot, e.PartialSession);
                return new OpenResult(OpenOutcome.RecoveryRequired, e.Message);
            }
            ReleaseLock(slot);
            SetClosed(slot, e.Message);
            return new OpenResult(OpenOutcome.Failed, e.Message);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before any process was created: release only what was acquired (spec §4.4).
            ReleaseLock(slot);
            SetClosed(slot, null);
            return new OpenResult(OpenOutcome.Cancelled);
        }
        catch (Exception e)
        {
            // Ownership after a partial initialization is uncertain.
            Update(slot, s => s with { Phase = LifecyclePhase.RecoveryRequired, LastError = e.Message });
            return new OpenResult(OpenOutcome.RecoveryRequired, e.Message);
        }

        slot.Session = session;
        if (ct.IsCancellationRequested)
        {
            // A cancelled initialization that later completes is disposed, never resurrected (spec §4.6).
            var closed = await ShutdownSessionAsync(slot, session);
            return new OpenResult(closed.Outcome == CloseOutcome.RecoveryRequired ? OpenOutcome.RecoveryRequired : OpenOutcome.Cancelled, closed.Message);
        }

        var now = _time.GetUtcNow();
        Update(slot, s => s with { Phase = LifecyclePhase.Open, BrowserProcessId = session.BrowserProcessId, EffectiveRuntimeVersion = session.RuntimeVersion, LastActivatedAt = now });
        var fresh = _repository.Get(id);
        if (fresh is not null) _repository.Update(fresh with { LastOpenedAt = now, LastAppliedRevision = revision, PendingRevision = fresh.ConfigRevision == revision ? null : fresh.PendingRevision });
        ObserveUnexpectedExit(slot, session);
        return new OpenResult(OpenOutcome.Opened);
    }

    /// <summary>Closes a profile, including one that is still starting.</summary>
    public async Task<CloseResult> CloseAsync(Guid id)
    {
        var slot = GetSlot(id);
        lock (_sync) slot.StartCts?.Cancel();
        await slot.Gate.WaitAsync();
        try { return await CloseCoreAsync(slot); }
        finally { slot.Gate.Release(); }
    }

    private async Task<CloseResult> CloseCoreAsync(Slot slot)
    {
        switch (slot.State.Phase)
        {
            case LifecyclePhase.Closed:
                return new CloseResult(CloseOutcome.AlreadyClosed);
            case LifecyclePhase.RecoveryRequired:
                return await RetryRecoveryCoreAsync(slot);
        }
        if (slot.Session is null) { ReleaseLock(slot); SetClosed(slot, null); return new CloseResult(CloseOutcome.Closed); }
        return await ShutdownSessionAsync(slot, slot.Session);
    }

    /// <summary>Stops only the generation that reported a security failure; a late callback cannot close its successor.</summary>
    public async Task<CloseResult> StopGenerationAsync(GenerationContext context, string reason)
    {
        var slot = GetSlot(context.ProfileId);
        await slot.Gate.WaitAsync();
        try
        {
            if (slot.State.Generation != context.GenerationId || slot.State.Phase == LifecyclePhase.Closed)
                return new CloseResult(CloseOutcome.AlreadyClosed);
            var result = await CloseCoreAsync(slot);
            Update(slot, state => state with { LastError = result.Outcome == CloseOutcome.RecoveryRequired
                ? reason + " " + state.LastError : reason });
            return result;
        }
        finally { slot.Gate.Release(); }
    }

    private async Task<CloseResult> ShutdownSessionAsync(Slot slot, IBrowserSession session)
    {
        Update(slot, s => s with { Phase = LifecyclePhase.Closing });
        try
        {
            await session.CloseAsync();
        }
        catch (Exception e)
        {
            Update(slot, s => s with { LastError = e.Message });
        }
        return await AwaitExitAsync(slot, session);
    }

    private async Task<CloseResult> AwaitExitAsync(Slot slot, IBrowserSession session)
    {
        var exited = session.ProcessExited;
        if (!exited.IsCompleted)
        {
            using var cts = new CancellationTokenSource();
            var delay = Task.Delay(_exitTimeout, _time, cts.Token);
            var winner = await Task.WhenAny(exited, delay);
            if (winner != exited)
            {
                // Keep the lock and the UDF untouched; never kill unrelated msedgewebview2 processes (spec §4.4, A23).
                Update(slot, s => s with { Phase = LifecyclePhase.RecoveryRequired, LastError = "Процесс браузера не завершился вовремя." });
                return new CloseResult(CloseOutcome.RecoveryRequired, "Не удалось подтвердить завершение процесса браузера. Повторите попытку позже.");
            }
            cts.Cancel();
        }
        Update(slot, state => state with { WebRtcReadback = session.WebRtcReadback, AudioReadback = session.AudioReadback });
        slot.Session = null;
        ReleaseLock(slot);
        SetClosed(slot, slot.State.Phase == LifecyclePhase.RecoveryRequired ? slot.State.LastError : null);
        return new CloseResult(CloseOutcome.Closed);
    }

    /// <summary>Retries a RecoveryRequired profile by awaiting the retained exit signal of the same generation.</summary>
    public async Task<CloseResult> RetryRecoveryAsync(Guid id)
    {
        var slot = GetSlot(id);
        await slot.Gate.WaitAsync();
        try { return await RetryRecoveryCoreAsync(slot); }
        finally { slot.Gate.Release(); }
    }

    private async Task<CloseResult> RetryRecoveryCoreAsync(Slot slot)
    {
        if (slot.State.Phase != LifecyclePhase.RecoveryRequired) return new CloseResult(CloseOutcome.AlreadyClosed);
        if (slot.Session is null)
            return new CloseResult(CloseOutcome.RecoveryRequired, "Владение процессом не определено. Перезапустите Windows-сеанс или завершите процессы этого профиля вручную.");
        return await AwaitExitAsync(slot, slot.Session);
    }

    public async Task<OpenResult> RestartAsync(Guid id)
    {
        var close = await CloseAsync(id);
        if (close.Outcome == CloseOutcome.RecoveryRequired) return new OpenResult(OpenOutcome.RecoveryRequired, close.Message);
        return await OpenAsync(id);
    }

    /// <summary>Local session reset: closes, waits for release, removes only the UDF and app permission decisions (spec §4.7).</summary>
    public async Task<CleanupReport> ResetLocalSessionAsync(Guid id)
    {
        var slot = GetSlot(id);
        lock (_sync) slot.StartCts?.Cancel();
        await slot.Gate.WaitAsync();
        try
        {
            var opId = _repository.AddPendingOperation(id, PendingOperationKind.Reset, "Requested");
            var closed = await CloseCoreAsync(slot);
            if (closed.Outcome == CloseOutcome.RecoveryRequired)
                return new CleanupReport(false, null, closed.Message);
            return CompleteReset(id, opId);
        }
        finally { slot.Gate.Release(); }
    }

    private CleanupReport CompleteReset(Guid id, long opId)
    {
        _repository.UpdatePendingOperation(opId, "Cleaning");
        var files = _deleter.DeleteUserDataFolder(id);
        if (!files.IsComplete)
        {
            _repository.UpdatePendingOperation(opId, "CleanupPending", files.Reason);
            return new CleanupReport(false, files, files.Reason);
        }
        _repository.DeletePermissions(id);
        _repository.CompletePendingOperation(opId);
        return new CleanupReport(true, files, null);
    }

    /// <summary>Local deletion: persist intent, close, remove owned data and secrets, then metadata (spec §4.7).</summary>
    public async Task<CleanupReport> DeleteLocalProfileAsync(Guid id)
    {
        var slot = GetSlot(id);
        lock (_sync) slot.StartCts?.Cancel();
        await slot.Gate.WaitAsync();
        try
        {
            var opId = _repository.AddPendingOperation(id, PendingOperationKind.Delete, "Requested");
            var closed = await CloseCoreAsync(slot);
            if (closed.Outcome == CloseOutcome.RecoveryRequired)
                return new CleanupReport(false, null, closed.Message);
            return CompleteDelete(id, opId);
        }
        finally { slot.Gate.Release(); }
    }

    private CleanupReport CompleteDelete(Guid id, long opId)
    {
        _repository.UpdatePendingOperation(opId, "Cleaning");
        var files = _deleter.DeleteProfileDirectory(id);
        if (!files.IsComplete)
        {
            _repository.UpdatePendingOperation(opId, "CleanupPending", files.Reason);
            return new CleanupReport(false, files, files.Reason);
        }
        _credentials.DeleteAllForProfile(id);
        try { File.Delete(_paths.LockFile(id)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _repository.DeleteMetadata(id); // cascades permissions, confirmations, revisions
        _repository.CompletePendingOperation(opId);
        lock (_sync) _slots.Remove(id);
        return new CleanupReport(true, files, null);
    }

    /// <summary>Resumes interrupted reset/delete cleanup at startup without opening any profile (spec §4.7, §6.3).</summary>
    public async Task<IReadOnlyList<(PendingOperation Operation, CleanupReport Report)>> ResumePendingOperationsAsync()
    {
        var results = new List<(PendingOperation, CleanupReport)>();
        foreach (var op in _repository.ListPendingOperations())
        {
            var slot = GetSlot(op.ProfileId);
            await slot.Gate.WaitAsync();
            try
            {
                if (slot.State.Phase != LifecyclePhase.Closed) continue;
                if (!ProfileLock.TryAcquire(_paths, op.ProfileId, out var lk))
                {
                    results.Add((op, new CleanupReport(false, null, "Профиль занят другим экземпляром.")));
                    continue;
                }
                using (lk)
                {
                    CleanupReport report = op.Kind switch
                    {
                        PendingOperationKind.Delete => CompleteDelete(op.ProfileId, op.Id),
                        PendingOperationKind.Reset => CompleteReset(op.ProfileId, op.Id),
                        _ => CompleteInterruptedNonCleanup(op),
                    };
                    results.Add((op, report));
                }
            }
            finally { slot.Gate.Release(); }
        }
        return results;
    }

    private CleanupReport CompleteInterruptedNonCleanup(PendingOperation op)
    {
        // Restart/network changes interrupted by a crash: keep the pending revision for the user to retry or revert;
        // never auto-launch the account.
        _repository.CompletePendingOperation(op.Id);
        return new CleanupReport(true, null, "Операция прервана; профиль закрыт. Ожидающие изменения сохранены.");
    }

    private void ObserveUnexpectedExit(Slot slot, IBrowserSession session) => _ = ObserveExitAsync(slot, session, recovery: false);

    private void ObserveRecovery(Slot slot, IBrowserSession session) => _ = ObserveExitAsync(slot, session, recovery: true);

    private async Task ObserveExitAsync(Slot slot, IBrowserSession session, bool recovery)
    {
        // Capture the caller's WPF context; ContinueWith(TaskScheduler.Current) used
        // the pool and left a nested task and dead controllers behind after a crash.
        try { await session.ProcessExited; }
        catch (Exception e) { Update(slot, s => s with { LastError = "Не удалось подтвердить выход браузера: " + e.Message }, notifySafely: true); return; }
        await slot.Gate.WaitAsync();
        try
        {
            var expected = recovery ? LifecyclePhase.RecoveryRequired : LifecyclePhase.Open;
            if (!ReferenceEquals(slot.Session, session) || slot.State.Phase != expected) return;
            var message = recovery ? slot.State.LastError ?? "Восстановлено после ошибки запуска."
                : "Процесс браузера завершился неожиданно.";
            Update(slot, state => state with { WebRtcReadback = session.WebRtcReadback, AudioReadback = session.AudioReadback }, notifySafely: true);
            await session.CloseAsync();
            slot.Session = null;
            ReleaseLock(slot);
            SetClosed(slot, message, notifySafely: true);
        }
        catch (Exception e)
        {
            Update(slot, s => s with { Phase = LifecyclePhase.RecoveryRequired, LastError = "Не удалось освободить контроллеры браузера: " + e.Message }, notifySafely: true);
        }
        finally { slot.Gate.Release(); }
    }

    private static void ReleaseLock(Slot slot)
    {
        slot.Lock?.Dispose();
        slot.Lock = null;
    }

    private void SetClosed(Slot slot, string? error, bool notifySafely = false) =>
        Update(slot, s => s with { Phase = LifecyclePhase.Closed, BrowserProcessId = null, LastError = error, ActiveRevision = null }, notifySafely);

    private void Update(Slot slot, Func<ProfileRuntimeState, ProfileRuntimeState> change, bool notifySafely = false)
    {
        ProfileRuntimeState next;
        lock (_sync) next = slot.State = change(slot.State);
        var subscribers = StateChanged;
        if (!notifySafely) { subscribers?.Invoke(next); return; }
        // An exit observer has no awaiting caller. A broken subscriber must not
        // prevent native cleanup, lock release or notification of other listeners.
        foreach (Action<ProfileRuntimeState> subscriber in subscribers?.GetInvocationList() ?? [])
        {
            try { subscriber(next); }
            catch (Exception error)
            {
                lock (_sync) slot.State = slot.State with { LastError = (slot.State.LastError is { } prior ? prior + " " : "")
                    + "Ошибка обработчика состояния: " + error.Message };
            }
        }
    }
}
