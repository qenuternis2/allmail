using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Reminders;

namespace ProtonProfiles.Core.Tests;

public class ReminderTests
{
    private static ProfileConfig P(int months = 6) => new() { Id = Guid.NewGuid(), DisplayName = "A", ReminderMonths = months };

    private static TimeZoneInfo Zone(string iana) => TimeZoneInfo.FindSystemTimeZoneById(iana);

    [Theory]
    [InlineData("2026-08-31", 6, "2027-02-28")]
    [InlineData("2027-08-31", 6, "2028-02-29")]
    [InlineData("2026-01-31", 1, "2026-02-28")]
    [InlineData("2026-03-31", 1, "2026-04-30")]
    [InlineData("2026-05-15", 12, "2027-05-15")]
    public void Calendar_months_clamp_to_month_end(string start, int months, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), ReminderCalculator.AddCalendarMonths(DateOnly.Parse(start), months));

    [Fact]
    public void No_confirmation_reports_no_check_recorded()
    {
        var s = ReminderCalculator.Evaluate(P(), DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        Assert.Equal(ReminderState.NoCheckRecorded, s.State);
    }

    [Fact]
    public void Due_date_uses_original_local_date_and_zone()
    {
        // 23:30 UTC on Aug 31 is already Sep 1 in Moscow.
        var confirmedAt = new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero);
        var p = ReminderCalculator.ConfirmVisit(P(6), confirmedAt, Zone("Europe/Moscow"));
        Assert.Equal(new DateOnly(2026, 9, 1), p.ConfirmationLocalDate);
        Assert.Equal(new DateOnly(2027, 3, 1), ReminderCalculator.ComputeDueDate(p));

        // A later OS timezone change does not move the anniversary.
        var dayBefore = new DateTimeOffset(2027, 2, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(ReminderState.NotDue, ReminderCalculator.Evaluate(p, dayBefore, Zone("America/Los_Angeles")).State);
        var dueDay = new DateTimeOffset(2027, 2, 28, 21, 30, 0, TimeSpan.Zero); // Mar 1 00:30 in Moscow
        Assert.Equal(ReminderState.Due, ReminderCalculator.Evaluate(p, dueDay, Zone("America/Los_Angeles")).State);
    }

    [Fact]
    public void Snooze_is_independent_and_never_creates_a_visit()
    {
        var p = ReminderCalculator.ConfirmVisit(P(1), new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        var now = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(ReminderState.Due, ReminderCalculator.Evaluate(p, now, TimeZoneInfo.Utc).State);
        var snoozed = ReminderCalculator.Snooze(p, now.AddDays(7));
        Assert.Equal(p.LastUserConfirmedVisitAt, snoozed.LastUserConfirmedVisitAt);
        Assert.Equal(ReminderState.Snoozed, ReminderCalculator.Evaluate(snoozed, now, TimeZoneInfo.Utc).State);
        Assert.Equal(ReminderState.Due, ReminderCalculator.Evaluate(snoozed, now.AddDays(8), TimeZoneInfo.Utc).State);
    }

    [Fact]
    public void Undo_restores_previous_confirmation()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        env.Catalog.ConfirmVisit(a.Id, TimeZoneInfo.Utc);
        var first = env.Repository.Get(a.Id)!.LastUserConfirmedVisitAt;
        env.Catalog.ConfirmVisit(a.Id, TimeZoneInfo.Utc);
        var history = env.Repository.ListVisitConfirmations(a.Id);
        Assert.Equal(2, history.Count);
        var restored = env.Catalog.UndoVisitConfirmation(a.Id, history[0].Id);
        Assert.Equal(first, restored.LastUserConfirmedVisitAt);
        restored = env.Catalog.UndoVisitConfirmation(a.Id, env.Repository.ListVisitConfirmations(a.Id)[0].Id);
        Assert.Null(restored.LastUserConfirmedVisitAt);
        Assert.Null(restored.ConfirmationLocalDate);
    }

    [Fact]
    public void History_is_bounded()
    {
        using var env = new TestEnv();
        var a = env.AddProfile();
        for (var i = 0; i < Persistence.SqliteProfileRepository.VisitHistoryLimit + 10; i++)
            env.Repository.AddVisitConfirmation(a.Id, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow), "UTC");
        Assert.Equal(Persistence.SqliteProfileRepository.VisitHistoryLimit, env.Repository.ListVisitConfirmations(a.Id).Count);
    }

    [Fact]
    public void Opening_a_profile_is_not_a_confirmed_visit()
    {
        var p = P() with { LastOpenedAt = DateTimeOffset.UtcNow };
        Assert.Equal(ReminderState.NoCheckRecorded, ReminderCalculator.Evaluate(p, DateTimeOffset.UtcNow, TimeZoneInfo.Utc).State);
    }
}
