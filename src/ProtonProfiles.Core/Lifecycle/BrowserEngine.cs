using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Lifecycle;

/// <summary>Immutable context carried by every asynchronous browser callback (spec §4.3).</summary>
public readonly record struct GenerationContext(Guid ProfileId, long GenerationId)
{
    public override string ToString() => $"{ProfileId:D}#{GenerationId}";
}

/// <summary>Everything the engine needs to create one generation. Snapshot values only; no mutable "current profile".</summary>
public sealed record BrowserStartRequest(
    GenerationContext Context,
    ProfileConfig Config,
    long Revision,
    string UserDataFolder,
    Func<GenerationContext, bool> IsCurrentGeneration);

/// <summary>One live environment generation of a profile.</summary>
public interface IBrowserSession
{
    GenerationContext Context { get; }

    /// <summary>
    /// Completes when the engine observes browser-process exit for THIS generation (BrowserProcessExited, S16).
    /// The task is retained, so an exit already observed is never awaited twice.
    /// </summary>
    Task ProcessExited { get; }

    int? BrowserProcessId { get; }
    string? RuntimeVersion { get; }
    WebRtcReadbackSummary? WebRtcReadback => null;
    PageGuardReadbackSummary? AudioReadback => null;

    /// <summary>Closes children and controllers and disposes the controls. Must be called on the UI thread.</summary>
    Task CloseAsync();
}

/// <summary>Thrown by <see cref="IBrowserEngine.StartAsync"/>. <see cref="ProcessMayExist"/> drives RecoveryRequired (spec §4.4).</summary>
public sealed class BrowserStartException : Exception
{
    public bool ProcessMayExist { get; }
    public IBrowserSession? PartialSession { get; }

    public BrowserStartException(string message, bool processMayExist, IBrowserSession? partialSession = null, Exception? inner = null)
        : base(message, inner)
    {
        ProcessMayExist = processMayExist;
        PartialSession = partialSession;
    }
}

public interface IBrowserEngine
{
    BrowserCapabilities Capabilities { get; }

    /// <summary>
    /// Deterministic initialization (spec §4.5): environment with options → effective UDF check → controller options
    /// → handlers and settings → navigate. Called on the UI thread; implementations must observe <paramref name="cancellationToken"/>
    /// only at safe points and otherwise return the session so the caller can dispose it.
    /// </summary>
    Task<IBrowserSession> StartAsync(BrowserStartRequest request, CancellationToken cancellationToken);
}
