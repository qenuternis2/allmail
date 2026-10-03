using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Reminders;

public enum ReminderState
{
    /// <summary>"Дата проверки не записана".</summary>
    NoCheckRecorded,
    NotDue,
    Due,
    Snoozed,
}

public sealed record ReminderStatus(ReminderState State, DateOnly? DueDate, string? TimeZoneId);

/// <summary>
/// Calendar-month reminders based on the user's confirmed visit (spec §8). The due date is computed from the
/// confirmation's original local date and timezone so later OS timezone changes do not move the anniversary.
/// These are local bookkeeping values, never Proton's server-side activity.
/// </summary>
public static class ReminderCalculator
{
    public const string PolicyHelpUrl = "https://proton.me/support/inactive-accounts";
    public const string PolicyReviewedOn = "2026-10-03";

    /// <summary>Adds calendar months, clamping to the last valid day (e.g. Aug 31 + 6 → Feb 28/29).</summary>
    public static DateOnly AddCalendarMonths(DateOnly localDate, int months) => localDate.AddMonths(months);

    public static DateOnly? ComputeDueDate(ProfileConfig profile) =>
        profile.ConfirmationLocalDate is { } date ? AddCalendarMonths(date, profile.ReminderMonths) : null;

    public static ReminderStatus Evaluate(ProfileConfig profile, DateTimeOffset nowUtc, TimeZoneInfo currentZone)
    {
        if (profile.LastUserConfirmedVisitAt is null || profile.ConfirmationLocalDate is null)
            return new ReminderStatus(ReminderState.NoCheckRecorded, null, null);

        var due = ComputeDueDate(profile)!.Value;
        var zone = ResolveZone(profile.ConfirmationTimeZoneId) ?? currentZone;
        var todayInBasisZone = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);

        if (todayInBasisZone < due) return new ReminderStatus(ReminderState.NotDue, due, profile.ConfirmationTimeZoneId);
        if (profile.SnoozedUntil is { } snooze && snooze > nowUtc)
            return new ReminderStatus(ReminderState.Snoozed, due, profile.ConfirmationTimeZoneId);
        return new ReminderStatus(ReminderState.Due, due, profile.ConfirmationTimeZoneId);
    }

    /// <summary>Records a user-confirmed visit, capturing the local date and timezone basis.</summary>
    public static ProfileConfig ConfirmVisit(ProfileConfig profile, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        return profile with
        {
            LastUserConfirmedVisitAt = nowUtc.ToUniversalTime(),
            ConfirmationLocalDate = DateOnly.FromDateTime(local.DateTime),
            ConfirmationTimeZoneId = zone.Id,
            // A new confirmation clears an older snooze; a snooze never creates a visit.
            SnoozedUntil = null,
        };
    }

    public static ProfileConfig Snooze(ProfileConfig profile, DateTimeOffset until) => profile with { SnoozedUntil = until.ToUniversalTime() };

    /// <summary>Recomputes the visit fields from the remaining history after an accidental mark is removed.</summary>
    public static ProfileConfig ApplyLatestConfirmation(ProfileConfig profile, VisitConfirmation? latest) =>
        latest is null
            ? profile with { LastUserConfirmedVisitAt = null, ConfirmationLocalDate = null, ConfirmationTimeZoneId = null }
            : profile with { LastUserConfirmedVisitAt = latest.ConfirmedAtUtc, ConfirmationLocalDate = latest.LocalDate, ConfirmationTimeZoneId = latest.TimeZoneId };

    private static TimeZoneInfo? ResolveZone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}

public sealed record VisitConfirmation(long Id, Guid ProfileId, DateTimeOffset ConfirmedAtUtc, DateOnly LocalDate, string TimeZoneId);
