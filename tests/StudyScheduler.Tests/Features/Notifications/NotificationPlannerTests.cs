using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationPlannerTests
{
    private const long Tutor = 555;
    private const int Lookback = 60;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly NotificationPlanner _sut = new();

    private static TutorProfile Profile(int? remind, bool followUp)
    {
        var profile = TutorProfile.Create(Tutor, TimeZoneInfo.Utc, CreatedAt).Value;
        profile.UpdateRemindMinutes(remind);
        profile.UpdateNotifyAfterLesson(followUp);
        return profile;
    }

    private static ScheduleEntry Entry(
        DateTimeOffset start,
        DateTimeOffset end,
        LessonStatus status = LessonStatus.Scheduled,
        NotificationState? notifications = null) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), SeriesId: null, OccurrenceDate: null,
            start, end, (int)(end - start).TotalMinutes, status,
            Price: 0m, IsPaid: false, Topic: null, Description: null, IsVirtual: false,
            CreatedAtUtc: start, notifications ?? NotificationState.None);

    private IReadOnlyList<DueNotification> Plan(TutorProfile profile, ScheduleEntry entry) =>
        _sut.Plan(profile, [entry], Now, Lookback);

    // --- Reminder ---

    [Fact]
    public void Plan_ReminderWindowOpen_ReturnsReminderDue()
    {
        // Arrange
        var entry = Entry(Now.AddMinutes(15), Now.AddMinutes(75));

        // Act
        var due = Plan(Profile(remind: 30, followUp: false), entry);

        // Assert
        Assert.Equal(NotificationKind.Reminder, Assert.Single(due).Kind);
    }

    [Fact]
    public void Plan_BeforeReminderLeadWindow_ReturnsNothing()
    {
        // Arrange
        // start - 30 = Now + 15 > Now → not yet due.
        var entry = Entry(Now.AddMinutes(45), Now.AddMinutes(105));

        // Act
        var due = Plan(Profile(remind: 30, followUp: false), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_ReminderAtLessonStart_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(Now, Now.AddMinutes(60));

        // Act
        var due = Plan(Profile(remind: 30, followUp: false), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_ReminderAlreadySent_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(
            Now.AddMinutes(15), Now.AddMinutes(75),
            notifications: NotificationState.None.WithReminderSent(Now.AddMinutes(-1)));

        // Act
        var due = Plan(Profile(remind: 30, followUp: false), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_RemindMinutesNull_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(Now.AddMinutes(15), Now.AddMinutes(75));

        // Act
        var due = Plan(Profile(remind: null, followUp: false), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_ReminderOnCancelledLesson_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(Now.AddMinutes(15), Now.AddMinutes(75), status: LessonStatus.Cancelled);

        // Act
        var due = Plan(Profile(remind: 30, followUp: false), entry);

        // Assert
        Assert.Empty(due);
    }

    // --- Follow-up ---

    [Fact]
    public void Plan_FollowUpInsideLookback_ReturnsFollowUpDue()
    {
        // Arrange
        var entry = Entry(Now.AddMinutes(-70), Now.AddMinutes(-10));

        // Act
        var due = Plan(Profile(remind: null, followUp: true), entry);

        // Assert
        Assert.Equal(NotificationKind.FollowUp, Assert.Single(due).Kind);
    }

    [Fact]
    public void Plan_FollowUpBeforeLessonEnd_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(Now.AddMinutes(-10), Now.AddMinutes(10));

        // Act
        var due = Plan(Profile(remind: null, followUp: true), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_FollowUpOutsideLookback_ReturnsNothing()
    {
        // Arrange
        // end = Now - 90 < Now - 60 → outside the lookback window.
        var entry = Entry(Now.AddMinutes(-150), Now.AddMinutes(-90));

        // Act
        var due = Plan(Profile(remind: null, followUp: true), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_FollowUpAlreadySent_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(
            Now.AddMinutes(-70), Now.AddMinutes(-10),
            notifications: NotificationState.None.WithFollowUpSent(Now.AddMinutes(-5)));

        // Act
        var due = Plan(Profile(remind: null, followUp: true), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_FollowUpDisabled_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(Now.AddMinutes(-70), Now.AddMinutes(-10));

        // Act
        var due = Plan(Profile(remind: null, followUp: false), entry);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_FollowUpOnCancelledLesson_ReturnsNothing()
    {
        // Arrange
        var entry = Entry(Now.AddMinutes(-70), Now.AddMinutes(-10), status: LessonStatus.Cancelled);

        // Act
        var due = Plan(Profile(remind: null, followUp: true), entry);

        // Assert
        Assert.Empty(due);
    }
}
