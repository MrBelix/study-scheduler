using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private const int Lookback = 30;

    private static TutorProfile Profile(int? remindMinutes = 30, bool notifyAfter = true)
    {
        var profile = TutorProfile.Create(555, TimeZoneInfo.Utc, Now.AddDays(-10)).Value;
        profile.UpdateRemindMinutes(remindMinutes);
        profile.UpdateNotifyAfterLesson(notifyAfter);
        return profile;
    }

    private static ScheduleSlot Slot(
        DateTimeOffset startUtc,
        LessonStatus status = LessonStatus.Scheduled,
        Guid? id = null,
        Guid? seriesId = null,
        DateOnly? occurrenceDate = null) =>
        new(
            id,
            Guid.NewGuid(),
            seriesId,
            occurrenceDate,
            startUtc,
            startUtc.AddMinutes(60),
            60,
            status,
            100m,
            IsPaid: false,
            Topic: null,
            Description: null,
            IsVirtual: id is null,
            CreatedAtUtc: Now.AddDays(-1));

    [Fact]
    public void Plans_reminder_for_slot_starting_within_lead_window()
    {
        var slot = Slot(Now.AddMinutes(20), id: Guid.NewGuid());

        var planned = NotificationPlanner.Plan(Profile(), [slot], Now, Lookback);

        var single = Assert.Single(planned);
        Assert.Equal(LessonNotificationKind.Reminder, single.Kind);
        Assert.Equal($"L:{slot.Id}", single.SlotKey);
    }

    [Fact]
    public void Does_not_remind_for_slot_beyond_lead_or_already_started()
    {
        var tooFar = Slot(Now.AddMinutes(31), id: Guid.NewGuid());
        var started = Slot(Now.AddMinutes(-1), id: Guid.NewGuid());

        // The started slot hasn't ended either, so no follow-up shows up here.
        Assert.Empty(NotificationPlanner.Plan(Profile(), [tooFar, started], Now, Lookback));
    }

    [Fact]
    public void Plans_follow_up_for_slot_ended_within_lookback()
    {
        var seriesId = Guid.NewGuid();
        var date = new DateOnly(2026, 7, 8);
        var slot = Slot(Now.AddMinutes(-70), seriesId: seriesId, occurrenceDate: date); // ended 10 min ago

        var planned = NotificationPlanner.Plan(Profile(), [slot], Now, Lookback);

        var single = Assert.Single(planned);
        Assert.Equal(LessonNotificationKind.FollowUp, single.Kind);
        // Series slots key by series + original date so the key survives materialization.
        Assert.Equal($"S:{seriesId}:2026-07-08", single.SlotKey);
    }

    [Fact]
    public void Does_not_follow_up_for_slot_ended_before_lookback()
    {
        var slot = Slot(Now.AddMinutes(-95), id: Guid.NewGuid()); // ended 35 min ago

        Assert.Empty(NotificationPlanner.Plan(Profile(), [slot], Now, Lookback));
    }

    [Theory]
    [InlineData(LessonStatus.Completed)]
    [InlineData(LessonStatus.Cancelled)]
    public void Skips_non_scheduled_slots(LessonStatus status)
    {
        var upcoming = Slot(Now.AddMinutes(10), status, id: Guid.NewGuid());
        var ended = Slot(Now.AddMinutes(-70), status, id: Guid.NewGuid());

        Assert.Empty(NotificationPlanner.Plan(Profile(), [upcoming, ended], Now, Lookback));
    }

    [Fact]
    public void Respects_disabled_settings()
    {
        var upcoming = Slot(Now.AddMinutes(10), id: Guid.NewGuid());
        var ended = Slot(Now.AddMinutes(-70), id: Guid.NewGuid());

        Assert.Empty(NotificationPlanner.Plan(Profile(remindMinutes: null, notifyAfter: false), [upcoming, ended], Now, Lookback));

        var remindersOnly = NotificationPlanner.Plan(Profile(notifyAfter: false), [upcoming, ended], Now, Lookback);
        Assert.Equal(LessonNotificationKind.Reminder, Assert.Single(remindersOnly).Kind);

        var followUpsOnly = NotificationPlanner.Plan(Profile(remindMinutes: null), [upcoming, ended], Now, Lookback);
        Assert.Equal(LessonNotificationKind.FollowUp, Assert.Single(followUpsOnly).Kind);
    }
}
