namespace ProtonProfiles.Core.Privacy;

public enum WebRtcReadbackOutcome { Verified, Violation, Unavailable, Obsolete }
public enum WebRtcReadbackScope { MainDocument, ChildDocument, Frame, DiagnosticDocument }

/// <summary>Observation metadata only; never contains URLs, script results, or exception messages.</summary>
public sealed record WebRtcReadbackSummary(int Verified, int Violations, int Unavailable, int Obsolete,
    WebRtcReadbackOutcome? LastOutcome, WebRtcReadbackScope? LastScope)
{
    public static WebRtcReadbackSummary Empty { get; } = new(0, 0, 0, 0, null, null);
    public WebRtcReadbackSummary Record(WebRtcReadbackOutcome outcome, WebRtcReadbackScope scope) => new(
        Verified + (outcome == WebRtcReadbackOutcome.Verified ? 1 : 0),
        Violations + (outcome == WebRtcReadbackOutcome.Violation ? 1 : 0),
        Unavailable + (outcome == WebRtcReadbackOutcome.Unavailable ? 1 : 0),
        Obsolete + (outcome == WebRtcReadbackOutcome.Obsolete ? 1 : 0), outcome, scope);
}

public static class WebRtcGuardReadback
{
    /// <summary>
    /// WebView2 returns JSON null when a script cannot run or throws. This is missing evidence, not false.
    /// Only an explicit false from the same still-live document establishes a descriptor violation.
    /// </summary>
    public static async Task<WebRtcReadbackOutcome> ObserveAsync(Func<Task<string>> observe, Func<bool> isCurrentDocument)
    {
        return (await PageGuardReadback.ObserveAsync(observe, isCurrentDocument)) switch
        {
            PageGuardReadbackOutcome.Verified => WebRtcReadbackOutcome.Verified,
            PageGuardReadbackOutcome.Violation => WebRtcReadbackOutcome.Violation,
            PageGuardReadbackOutcome.Obsolete => WebRtcReadbackOutcome.Obsolete,
            _ => WebRtcReadbackOutcome.Unavailable,
        };
    }
}

/// <summary>STA-thread document lifetime tracking. An asynchronous result must not belong to an older navigation.</summary>
public sealed class WebRtcDocumentTracker : PageDocumentTracker { }

public class PageDocumentTracker
{
    private ulong? _navigationId;
    private long _revision;
    private bool _ready;
    private bool _destroyed;

    public void NavigationStarting(ulong navigationId)
    {
        if (_destroyed) return;
        _navigationId = navigationId;
        _revision++;
        _ready = false;
    }

    public Func<bool>? DocumentReady(ulong navigationId)
    {
        if (_destroyed || (_navigationId is { } current && current != navigationId)) return null;
        _navigationId = navigationId;
        _ready = true;
        var revision = _revision;
        return () => !_destroyed && _ready && _revision == revision;
    }

    public void Destroy()
    {
        _destroyed = true;
        _ready = false;
        _revision++;
    }
}
