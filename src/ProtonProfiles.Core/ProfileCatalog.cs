using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Interchange;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;
using ProtonProfiles.Core.Reminders;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core;

public sealed record SaveResult(bool Saved, bool RestartRequired, IReadOnlyList<string> Errors);

/// <summary>Metadata operations that need no browser: create, edit, search, reminders, import/export.</summary>
public sealed class ProfileCatalog
{
    private readonly IProfileRepository _repository;
    private readonly ICredentialStore _credentials;
    private readonly TimeProvider _time;

    public ProfileCatalog(IProfileRepository repository, ICredentialStore credentials, TimeProvider? time = null)
    {
        _repository = repository;
        _credentials = credentials;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<ProfileConfig> List() => _repository.ListProfiles();

    /// <summary>Case-insensitive search by name and label (F03).</summary>
    public static IEnumerable<ProfileConfig> Filter(IEnumerable<ProfileConfig> profiles, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return profiles;
        var q = query.Trim();
        return profiles.Where(p =>
            p.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (p.EmailLabel?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    /// <summary>F01: an empty local profile with a fresh UUID; its UDF is created lazily on first open.</summary>
    public SaveResult Create(string displayName, string? emailLabel, string color, out ProfileConfig? created,
        ProfileKind kind = ProfileKind.Mail, string? testStartUrl = null)
    {
        var existing = _repository.ListProfiles();
        created = new ProfileConfig
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName.Trim(),
            Kind = kind,
            TestStartUrl = testStartUrl,
            EmailLabel = string.IsNullOrWhiteSpace(emailLabel) ? null : emailLabel.Trim(),
            Color = color,
            SortOrder = existing.Count == 0 ? 0 : existing.Max(p => p.SortOrder) + 1,
            NetworkMode = NetworkMode.System,
        };
        var errors = ProfileValidator.Validate(created).ToList();
        if (kind == ProfileKind.Test && testStartUrl is null) errors.Add("Укажите начальный URL профиля.");
        if (errors.Count > 0) { created = null; return new SaveResult(false, false, errors); }
        var newId = created.Id;
        if (existing.Any(p => p.Id == newId)) throw new InvalidOperationException("UUID collision.");
        _repository.Insert(created);
        _repository.SaveRevisionSnapshot(created);
        return new SaveResult(true, false, []);
    }

    /// <summary>
    /// Saves edited settings as a new revision. When the profile is live and the change requires a restart, the new
    /// revision is recorded as pending until a generation starts with it.
    /// </summary>
    public SaveResult SaveSettings(ProfileConfig edited, bool profileIsLive)
    {
        var current = _repository.Get(edited.Id) ?? throw new InvalidOperationException("Профиль не найден.");
        var errors = ProfileValidator.Validate(edited);
        if (errors.Count > 0) return new SaveResult(false, false, errors);

        var restart = ProfileConfig.RequiresRestart(current, edited);
        var next = edited with
        {
            ConfigRevision = current.ConfigRevision + 1,
            LastAppliedRevision = current.LastAppliedRevision,
            PendingRevision = restart && profileIsLive ? current.ConfigRevision + 1 : null,
        };
        // Credential rotation: drop the reference the new revision no longer uses.
        if (current.Proxy?.CredentialRef is { } oldRef && oldRef != next.Proxy?.CredentialRef && !profileIsLive)
            _credentials.Delete(oldRef);
        _repository.SaveRevisionSnapshot(next);
        _repository.Update(next);
        return new SaveResult(true, restart && profileIsLive, []);
    }

    /// <summary>Explicit "Revert and restart" path for a failed pending revision (spec §6.3 step 6).</summary>
    public bool RevertToLastApplied(Guid id)
    {
        var current = _repository.Get(id);
        if (current?.LastAppliedRevision is not { } applied) return false;
        var snapshot = _repository.GetRevisionSnapshot(id, applied);
        if (snapshot is null) return false;
        _repository.Update(snapshot with
        {
            ConfigRevision = current.ConfigRevision + 1,
            LastAppliedRevision = applied,
            PendingRevision = null,
            // Local bookkeeping is not part of a configuration revision.
            LastOpenedAt = current.LastOpenedAt,
            LastUserConfirmedVisitAt = current.LastUserConfirmedVisitAt,
            ConfirmationLocalDate = current.ConfirmationLocalDate,
            ConfirmationTimeZoneId = current.ConfirmationTimeZoneId,
            SnoozedUntil = current.SnoozedUntil,
            SortOrder = current.SortOrder,
        });
        return true;
    }

    public void SetFavorite(Guid id, bool value) => Mutate(id, p => p with { IsFavorite = value });
    public void SetPinned(Guid id, bool value) => Mutate(id, p => p with { IsPinned = value });
    public void Reorder(IReadOnlyList<Guid> ordered) => _repository.Reorder(ordered);

    public ProfileConfig ConfirmVisit(Guid id, TimeZoneInfo zone)
    {
        var p = _repository.Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        var updated = ReminderCalculator.ConfirmVisit(p, _time.GetUtcNow(), zone);
        _repository.AddVisitConfirmation(id, updated.LastUserConfirmedVisitAt!.Value, updated.ConfirmationLocalDate!.Value, updated.ConfirmationTimeZoneId!);
        _repository.Update(updated);
        return updated;
    }

    /// <summary>Corrects an accidental mark (spec §8).</summary>
    public ProfileConfig UndoVisitConfirmation(Guid id, long confirmationId)
    {
        _repository.DeleteVisitConfirmation(confirmationId);
        var p = _repository.Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        var latest = _repository.ListVisitConfirmations(id).FirstOrDefault();
        var updated = ReminderCalculator.ApplyLatestConfirmation(p, latest);
        _repository.Update(updated);
        return updated;
    }

    public void Snooze(Guid id, TimeSpan duration) => Mutate(id, p => ReminderCalculator.Snooze(p, _time.GetUtcNow() + duration));

    public string Export(ExportOptions options) => SettingsInterchange.Export(_repository.ListProfiles(), options);

    public ImportResult PreviewImport(byte[] utf8)
    {
        var start = _repository.ListProfiles().Select(p => p.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        return SettingsInterchange.Import(utf8, start);
    }

    /// <summary>Commits a previewed import transactionally. Imported profiles stay closed (spec §9.1).</summary>
    public void CommitImport(ImportPreview preview)
    {
        var existingIds = _repository.ListProfiles().Select(p => p.Id).ToHashSet();
        if (preview.Profiles.Any(p => existingIds.Contains(p.Id))) throw new InvalidOperationException("UUID collision on import.");
        _repository.InsertMany(preview.Profiles);
        foreach (var p in preview.Profiles) _repository.SaveRevisionSnapshot(p);
    }

    private void Mutate(Guid id, Func<ProfileConfig, ProfileConfig> change)
    {
        var p = _repository.Get(id) ?? throw new InvalidOperationException("Профиль не найден.");
        _repository.Update(change(p));
    }
}
