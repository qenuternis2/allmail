namespace ProtonProfiles.Core.Lifecycle;

public sealed record LiveProfileInfo(Guid ProfileId, string DisplayName, Model.LifecyclePhase Phase, bool IsPinned, DateTimeOffset? LastActivatedAt);

public sealed record CapacityDecision(bool Allowed, IReadOnlyList<LiveProfileInfo> SuggestedToClose, IReadOnlyList<LiveProfileInfo> AllLive);

/// <summary>
/// At most N live environments including starting/closing/recovery ones (spec §4.4, A30). Never evicts silently:
/// when full, the caller must ask the user to close one or cancel. Pinned profiles are not suggested, but the
/// user may still choose them explicitly.
/// </summary>
public static class CapacityPolicy
{
    public const int DefaultMaxLiveEnvironments = 3;

    public static CapacityDecision Check(IReadOnlyCollection<LiveProfileInfo> live, Guid requested, int max = DefaultMaxLiveEnvironments)
    {
        if (live.Any(l => l.ProfileId == requested) || live.Count < max)
            return new CapacityDecision(true, [], live.ToList());

        var suggested = live
            .Where(l => !l.IsPinned && l.Phase == Model.LifecyclePhase.Open)
            .OrderBy(l => l.LastActivatedAt ?? DateTimeOffset.MinValue)
            .ToList();
        return new CapacityDecision(false, suggested, live.ToList());
    }
}
