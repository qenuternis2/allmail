using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Reminders;

namespace ProtonProfiles.Core.Persistence;

public sealed record PendingOperation(long Id, Guid ProfileId, PendingOperationKind Kind, string State, DateTimeOffset CreatedAtUtc, string? Detail);

public sealed record PermissionDecision(Guid ProfileId, string Origin, PermissionKindKey Kind, PermissionChoice Choice, DateTimeOffset DecidedAtUtc);

/// <summary>Application metadata only (spec §7, §9). Never stores secrets, cookies or message content.</summary>
public interface IProfileRepository
{
    IReadOnlyList<ProfileConfig> ListProfiles();
    ProfileConfig? Get(Guid id);
    void Insert(ProfileConfig profile);
    void InsertMany(IReadOnlyList<ProfileConfig> profiles);
    void Update(ProfileConfig profile);
    void DeleteMetadata(Guid id);
    void Reorder(IReadOnlyList<Guid> orderedIds);

    long AddVisitConfirmation(Guid profileId, DateTimeOffset atUtc, DateOnly localDate, string timeZoneId);
    IReadOnlyList<VisitConfirmation> ListVisitConfirmations(Guid profileId);
    void DeleteVisitConfirmation(long id);

    PermissionDecision? GetPermission(Guid profileId, string origin, PermissionKindKey kind);
    void SetPermission(PermissionDecision decision);
    void DeletePermissions(Guid profileId);
    IReadOnlyList<PermissionDecision> ListPermissions(Guid profileId);

    long AddPendingOperation(Guid profileId, PendingOperationKind kind, string state, string? detail = null);
    void UpdatePendingOperation(long id, string state, string? detail = null);
    void CompletePendingOperation(long id);
    IReadOnlyList<PendingOperation> ListPendingOperations();

    /// <summary>Stores an immutable snapshot of a revision; returns the new revision number.</summary>
    long SaveRevisionSnapshot(ProfileConfig profile);
    ProfileConfig? GetRevisionSnapshot(Guid profileId, long revision);
}
