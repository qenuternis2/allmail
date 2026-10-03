namespace ProtonProfiles.Core.Privacy;

public enum PageGuardReadbackOutcome { Verified, Violation, Unavailable, Obsolete }

/// <summary>Document observation counts only; scope is an application-defined context label, never a URL.</summary>
public sealed record PageGuardReadbackSummary(int Verified, int Violations, int Unavailable, int Obsolete,
    PageGuardReadbackOutcome? LastOutcome, string? LastScope)
{
    public static PageGuardReadbackSummary Empty { get; } = new(0, 0, 0, 0, null, null);
    public PageGuardReadbackSummary Record(PageGuardReadbackOutcome outcome, string scope) => new(
        Verified + (outcome == PageGuardReadbackOutcome.Verified ? 1 : 0),
        Violations + (outcome == PageGuardReadbackOutcome.Violation ? 1 : 0),
        Unavailable + (outcome == PageGuardReadbackOutcome.Unavailable ? 1 : 0),
        Obsolete + (outcome == PageGuardReadbackOutcome.Obsolete ? 1 : 0), outcome, scope);
}

public static class PageGuardReadback
{
    public static async Task<PageGuardReadbackOutcome> ObserveAsync(Func<Task<string>> observe, Func<bool> isCurrentDocument)
    {
        if (!isCurrentDocument()) return PageGuardReadbackOutcome.Obsolete;
        string result;
        try { result = await observe(); }
        catch (Exception) { return isCurrentDocument() ? PageGuardReadbackOutcome.Unavailable : PageGuardReadbackOutcome.Obsolete; }
        if (!isCurrentDocument()) return PageGuardReadbackOutcome.Obsolete;
        return result switch
        {
            "true" => PageGuardReadbackOutcome.Verified,
            "false" => PageGuardReadbackOutcome.Violation,
            _ => PageGuardReadbackOutcome.Unavailable,
        };
    }
}
