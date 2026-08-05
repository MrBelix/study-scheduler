using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationPlannerTests
{
    private const long Tutor = 555;
    private const int Grace = 15;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // Europe/London is on BST (UTC+1) in August, so every local time below is one hour ahead of UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateOnly Today = new(2026, 8, 5);
    private static readonly DateOnly Yesterday = new(2026, 8, 4);

    private readonly NotificationPlanner _sut = new();

    private static TutorProfile Profile(
        int? remind = 30,
        bool daySummary = false,
        bool morningAgenda = false,
        TimeOnly? agendaAt = null)
    {
        var profile = TutorProfile.Create(Tutor, London, CreatedAt).Value;
        profile.UpdateRemindMinutes(remind);
        profile.UpdateDaySummary(daySummary);
        profile.UpdateMorningAgenda(morningAgenda);
        if (agendaAt is { } at)
            profile.UpdateMorningAgendaAt(at);
        return profile;
    }

    /// <summary>A lesson at a LOCAL wall-clock slot of the tutor's zone, as the schedule stores it.</summary>
    private static Lesson At(
        DateOnly localDate,
        TimeOnly localStart,
        int duration = 60,
        LessonStatus status = LessonStatus.Scheduled)
    {
        var lesson = Lesson.Create(
            Guid.NewGuid(), WallClock.ToUtc(localDate, localStart, London), duration, 100m, CreatedAt).Value;
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        return lesson;
    }

    private static DateTimeOffset LocalNow(DateOnly localDate, TimeOnly localTime) =>
        WallClock.ToUtc(localDate, localTime, London);

    private static NotificationDispatch LiveReminder(Guid lessonId) =>
        NotificationDispatch.ForReminder(lessonId, Tutor, 1, RenderedSnapshot.Empty, "", CreatedAt, CreatedAt);

    private static NotificationDispatch LiveDay(NotificationKind kind, DateOnly localDate) =>
        NotificationDispatch.ForDay(kind, localDate, Tutor, 1, RenderedSnapshot.Empty, "", CreatedAt, CreatedAt);

    private IReadOnlyList<DueNotification> Plan(
        TutorProfile profile,
        DateTimeOffset nowUtc,
        IReadOnlyList<Lesson>? dayLessons = null,
        IReadOnlyList<Lesson>? reminderWindow = null,
        params NotificationDispatch[] live) =>
        _sut.Plan(profile, dayLessons ?? [], reminderWindow ?? [], live, nowUtc, Grace);

    // ---------- reminders ----------

    [Fact]
    public void Plan_ReminderWindowOpen_ReturnsReminderDue()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(15, 45));

        // Act
        var due = Plan(Profile(remind: 30), now, reminderWindow: [lesson]);

        // Assert
        var item = Assert.Single(due);
        Assert.Equal(NotificationKind.Reminder, item.Kind);
        Assert.Equal(lesson.Id, item.LessonId);
        Assert.Null(item.LocalDate);
    }

    [Fact]
    public void Plan_BeforeReminderLeadWindow_ReturnsNothing()
    {
        // Arrange — start - 30min is still 15 minutes away.
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(15, 15));

        // Act
        var due = Plan(Profile(remind: 30), now, reminderWindow: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_ReminderAtLessonStart_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(16, 0));

        // Act
        var due = Plan(Profile(remind: 30), now, reminderWindow: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_RemindMinutesNull_ReturnsNoReminder()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(15, 45));

        // Act
        var due = Plan(Profile(remind: null), now, reminderWindow: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_ReminderOnCancelledLesson_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0), status: LessonStatus.Cancelled);
        var now = LocalNow(Today, new TimeOnly(15, 45));

        // Act
        var due = Plan(Profile(remind: 30), now, reminderWindow: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_ReminderOnCompletedLesson_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0), status: LessonStatus.Completed);
        var now = LocalNow(Today, new TimeOnly(15, 45));

        // Act
        var due = Plan(Profile(remind: 30), now, reminderWindow: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_LessonWithLiveReminderDispatch_ReturnsNothing()
    {
        // Arrange — the live dispatch row IS the "already sent" fact.
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(15, 45));

        // Act
        var due = Plan(Profile(remind: 30), now, reminderWindow: [lesson], live: LiveReminder(lesson.Id));

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_LessonWhoseReminderWasRetracted_ReturnsReminderDue()
    {
        // Arrange — a retracted dispatch is simply absent from the live set, which is what re-arms the
        // reminder after the lesson was moved.
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(15, 45));
        var previous = LiveReminder(lesson.Id);
        previous.Retract(now);
        // The repository hands the planner the DELIVERED rows only, exactly as GetLiveAsync does.
        NotificationDispatch[] live = [.. new[] { previous }.Where(d => d.IsLive)];

        // Act
        var due = _sut.Plan(Profile(remind: 30), [], [lesson], live, now, Grace);

        // Assert
        Assert.Equal(lesson.Id, Assert.Single(due).LessonId);
    }

    // ---------- morning agenda ----------

    [Fact]
    public void Plan_MorningAgendaTimeReached_ReturnsAgendaDue()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(8, 0));

        // Act
        var due = Plan(
            Profile(remind: null, morningAgenda: true, agendaAt: new TimeOnly(8, 0)), now, dayLessons: [lesson]);

        // Assert
        var item = Assert.Single(due);
        Assert.Equal(NotificationKind.DayAgenda, item.Kind);
        Assert.Equal(Today, item.LocalDate);
        Assert.Null(item.LessonId);
    }

    [Fact]
    public void Plan_BeforeMorningAgendaTime_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(7, 59));

        // Act
        var due = Plan(
            Profile(remind: null, morningAgenda: true, agendaAt: new TimeOnly(8, 0)), now, dayLessons: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_MorningAgendaOptedOut_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(9, 0));

        // Act
        var due = Plan(Profile(remind: null, morningAgenda: false), now, dayLessons: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_AgendaOnDayWithEveryLessonCancelled_ReturnsAgendaDue()
    {
        // Arrange — a fully cancelled day still gets its "день вільний" agenda.
        var lesson = At(Today, new TimeOnly(16, 0), status: LessonStatus.Cancelled);
        var now = LocalNow(Today, new TimeOnly(9, 0));

        // Act
        var due = Plan(
            Profile(remind: null, morningAgenda: true, agendaAt: new TimeOnly(8, 0)), now, dayLessons: [lesson]);

        // Assert
        Assert.Equal(NotificationKind.DayAgenda, Assert.Single(due).Kind);
    }

    [Fact]
    public void Plan_AgendaOnEmptyDay_ReturnsNothing()
    {
        // Arrange
        var now = LocalNow(Today, new TimeOnly(9, 0));

        // Act
        var due = Plan(Profile(remind: null, morningAgenda: true, agendaAt: new TimeOnly(8, 0)), now);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_AgendaForPastLocalDate_ReturnsNothing()
    {
        // Arrange — yesterday's agenda is yesterday's news, whatever the caller passes.
        var lesson = At(Yesterday, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(9, 0));

        // Act
        var due = Plan(
            Profile(remind: null, morningAgenda: true, agendaAt: new TimeOnly(8, 0)), now, dayLessons: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_AgendaWithLiveDispatch_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(9, 0));

        // Act
        var due = Plan(
            Profile(remind: null, morningAgenda: true, agendaAt: new TimeOnly(8, 0)), now,
            dayLessons: [lesson], live: LiveDay(NotificationKind.DayAgenda, Today));

        // Assert
        Assert.Empty(due);
    }

    // ---------- evening summary ----------

    [Fact]
    public void Plan_SummaryGracePastLastLessonEnd_ReturnsSummaryDue()
    {
        // Arrange — the last lesson ends 18:00 local; 15 minutes of grace make it due at 18:15.
        var lessons = new[] { At(Today, new TimeOnly(16, 0)), At(Today, new TimeOnly(17, 0)) };
        var now = LocalNow(Today, new TimeOnly(18, 15));

        // Act
        var due = Plan(Profile(remind: null, daySummary: true), now, dayLessons: lessons);

        // Assert
        var item = Assert.Single(due);
        Assert.Equal(NotificationKind.DaySummary, item.Kind);
        Assert.Equal(Today, item.LocalDate);
    }

    [Fact]
    public void Plan_SummaryWithinGraceOfLastLessonEnd_ReturnsNothing()
    {
        // Arrange
        var lessons = new[] { At(Today, new TimeOnly(16, 0)), At(Today, new TimeOnly(17, 0)) };
        var now = LocalNow(Today, new TimeOnly(18, 14));

        // Act
        var due = Plan(Profile(remind: null, daySummary: true), now, dayLessons: lessons);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_SummaryOnDayEndingAfterLocalMidnight_WaitsForGracePastThatEnd()
    {
        // Arrange — a 23:30–00:30 lesson holds its day open past local midnight.
        var lesson = At(Yesterday, new TimeOnly(23, 30));
        var profile = Profile(remind: null, daySummary: true);

        // Act
        var atMidnightHalf = Plan(profile, LocalNow(Today, new TimeOnly(0, 30)), dayLessons: [lesson]);
        var afterGrace = Plan(profile, LocalNow(Today, new TimeOnly(0, 45)), dayLessons: [lesson]);

        // Assert
        Assert.Empty(atMidnightHalf);
        var item = Assert.Single(afterGrace);
        Assert.Equal(NotificationKind.DaySummary, item.Kind);
        Assert.Equal(Yesterday, item.LocalDate);
    }

    [Fact]
    public void Plan_SummaryForAPastDayThatEndedBeforeMidnight_ReturnsNothing()
    {
        // Arrange — a day the tutor has already left behind is not re-opened the next morning.
        var lesson = At(Yesterday, new TimeOnly(18, 0));
        var now = LocalNow(Today, new TimeOnly(9, 0));

        // Act
        var due = Plan(Profile(remind: null, daySummary: true), now, dayLessons: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_SummaryWithNoUnmarkedLesson_ReturnsNothing()
    {
        // Arrange — every lesson of the day is already settled, so there is nothing left to ask about.
        var lessons = new[]
        {
            At(Today, new TimeOnly(16, 0), status: LessonStatus.Completed),
            At(Today, new TimeOnly(17, 0), status: LessonStatus.Cancelled),
        };
        var now = LocalNow(Today, new TimeOnly(18, 30));

        // Act
        var due = Plan(Profile(remind: null, daySummary: true), now, dayLessons: lessons);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_SummaryOnDayWhereEveryLessonWasCancelled_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0), status: LessonStatus.Cancelled);
        var now = LocalNow(Today, new TimeOnly(18, 30));

        // Act
        var due = Plan(Profile(remind: null, daySummary: true), now, dayLessons: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_SummaryWithLiveDispatch_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(18, 30));

        // Act
        var due = Plan(
            Profile(remind: null, daySummary: true), now,
            dayLessons: [lesson], live: LiveDay(NotificationKind.DaySummary, Today));

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_SummaryWhoseDispatchWasRetracted_ReturnsSummaryDue()
    {
        // Arrange — a retracted row never blocks; only a live one does.
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(18, 30));
        var previous = LiveDay(NotificationKind.DaySummary, Today);
        previous.Retract(now);
        NotificationDispatch[] live = [.. new[] { previous }.Where(d => d.IsLive)];

        // Act
        var due = _sut.Plan(Profile(remind: null, daySummary: true), [lesson], [], live, now, Grace);

        // Assert
        Assert.Equal(NotificationKind.DaySummary, Assert.Single(due).Kind);
    }

    [Fact]
    public void Plan_DaySummaryOptedOut_ReturnsNothing()
    {
        // Arrange
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(18, 30));

        // Act
        var due = Plan(Profile(remind: null, daySummary: false), now, dayLessons: [lesson]);

        // Assert
        Assert.Empty(due);
    }

    [Fact]
    public void Plan_AgendaAndSummaryBothDue_ReturnsAgendaBeforeSummary()
    {
        // Arrange — the agenda hour passed long ago (nothing has been sent yet) and the day's only
        // lesson ended unmarked, so both digests come due on the same tick.
        var lesson = At(Today, new TimeOnly(16, 0));
        var now = LocalNow(Today, new TimeOnly(18, 30));
        var profile = Profile(remind: null, daySummary: true, morningAgenda: true, agendaAt: new TimeOnly(8, 0));

        // Act
        var due = _sut.Plan(profile, [lesson], [], [], now, Grace);

        // Assert
        Assert.Equal(
            new[] { NotificationKind.DayAgenda, NotificationKind.DaySummary },
            due.Select(d => d.Kind));
    }
}
