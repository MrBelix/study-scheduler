using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Core.Tenancy;
using StudyScheduler.Tests.Features.Lessons;
using StudyScheduler.Tests.Features.Reports;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationRunnerTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 777;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // Europe/London is on BST (UTC+1) in August, so every local time below is one hour ahead of UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateOnly Today = new(2026, 8, 5);

    private readonly RecordingTutorScope _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeStudentRepository _students;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentDebtReader _debts;
    private readonly FakeTutorProfileRepository _profiles;
    private readonly FakeNotificationDispatchRepository _dispatches;
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeNotificationSender _sender = new();
    private NotificationsOptions _options = new();

    public NotificationRunnerTests()
    {
        // The tick starts tenant-less and borrows each tutor's tenant in turn, so the repositories read
        // through the very scope the runner drives.
        _lessons = new FakeLessonRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _debts = new FakeStudentDebtReader(_lessons);
        _profiles = new FakeTutorProfileRepository(_tenant);
        _dispatches = new FakeNotificationDispatchRepository(_tenant);
    }

    private NotificationRunner Build(DateTimeOffset now)
    {
        var clock = new FixedClock(now);
        var renderer = new NotificationRenderer(Options.Create(_options));
        var views = new NotificationViewBuilder(_lessons, _students, _series, _debts, clock);
        var reconciler = new NotificationReconciler(
            _dispatches, views, renderer, _sender, _uow, clock, Options.Create(_options),
            NullLogger<NotificationReconciler>.Instance);

        return new NotificationRunner(
            _profiles, _lessons, _dispatches, _sender, new NotificationPlanner(), reconciler, views, renderer,
            Options.Create(_options), _uow, _tenant, clock, NullLogger<NotificationRunner>.Instance);
    }

    private static DateTimeOffset Utc(DateOnly localDate, TimeOnly localTime) =>
        WallClock.ToUtc(localDate, localTime, London);

    private TutorProfile AddProfile(
        int? remind = 30,
        bool daySummary = false,
        bool morningAgenda = false,
        TimeOnly? agendaAt = null,
        long tutorId = Tutor)
    {
        var profile = TutorProfile.Create(tutorId, London, CreatedAt).Value;
        profile.UpdateRemindMinutes(remind);
        profile.UpdateDaySummary(daySummary);
        profile.UpdateMorningAgenda(morningAgenda);
        if (agendaAt is { } at)
            profile.UpdateMorningAgendaAt(at);
        _profiles.Items.Add(profile);
        return profile;
    }

    private Guid AddStudent(string name = "Ann", long tutorId = Tutor)
    {
        // The pass has no tenant yet, so fixture rows are stamped the way persistence stamps them.
        var student = Student.Create(name, 100m, CreatedAt).Value.OwnedBy(tutorId);
        _students.Items.Add(student);
        return student.Id;
    }

    private Lesson AddLesson(
        Guid studentId,
        TimeOnly localStart,
        long tutorId = Tutor,
        LessonStatus status = LessonStatus.Scheduled)
    {
        var lesson = Lesson.Create(
            studentId, Utc(Today, localStart), 60, 500m, CreatedAt).Value.OwnedBy(tutorId);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    [Fact]
    public async Task RunAsync_DueReminder_SendsItAndRecordsTheDispatchWithItsRenderedHash()
    {
        // Arrange
        var studentId = AddStudent();
        AddProfile(remind: 30);
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var now = Utc(Today, new TimeOnly(15, 45));

        // Act
        await Build(now).RunAsync();

        // Assert
        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(Tutor, sent.ChatId);

        var dispatch = Assert.Single(_dispatches.Items);
        Assert.Equal(NotificationKind.Reminder, dispatch.Kind);
        Assert.Equal(lesson.Id, dispatch.LessonId);
        Assert.Equal(Tutor, dispatch.TutorTelegramId);
        // A reminder stops owing anything once its lesson ends.
        Assert.Equal(lesson.EndUtc, dispatch.ExpiresAtUtc);
        Assert.Equal(now, dispatch.SentAtUtc);
        Assert.True(dispatch.IsLive);
        // The hash and the snapshot are what every later re-render diffs against.
        Assert.Equal(64, dispatch.ContentHash.Length);
        Assert.Equal(lesson.Id, dispatch.Snapshot.Reminder!.LessonId);
    }

    [Fact]
    public async Task RunAsync_DeliveredReminder_StoresTheMessageIdTelegramReturned()
    {
        // Arrange
        var studentId = AddStudent();
        AddProfile(remind: 30);
        AddLesson(studentId, new TimeOnly(16, 0));
        _sender.NextMessageId = 4242;

        // Act
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Assert — the id is the handle every later edit of that message needs.
        Assert.Equal(4242, Assert.Single(_dispatches.Items).MessageId);
    }

    [Fact]
    public async Task RunAsync_TransientFailure_RecordsNoDispatch()
    {
        // Arrange
        var studentId = AddStudent();
        AddProfile(remind: 30);
        AddLesson(studentId, new TimeOnly(16, 0));
        _sender.Result = TelegramSendResult.TransientFailure;

        // Act
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Assert — nothing is recorded, so the very same lesson is retried next tick.
        Assert.Empty(_dispatches.Items);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task RunAsync_PermanentFailure_RecordsTheDispatchToStopTheRetryLoop()
    {
        // Arrange
        var studentId = AddStudent();
        AddProfile(remind: 30);
        AddLesson(studentId, new TimeOnly(16, 0));
        _sender.Result = TelegramSendResult.PermanentFailure;

        // Act
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Assert
        var dispatch = Assert.Single(_dispatches.Items);
        Assert.Null(dispatch.MessageId);
        Assert.True(dispatch.IsLive);
    }

    [Fact]
    public async Task RunAsync_MorningAgendaHourReached_SendsTheAgendaExpiringAtTheFollowingLocalMidnight()
    {
        // Arrange
        var studentId = AddStudent();
        AddProfile(remind: null, morningAgenda: true, agendaAt: new TimeOnly(8, 0));
        AddLesson(studentId, new TimeOnly(16, 0));

        // Act
        await Build(Utc(Today, new TimeOnly(8, 0))).RunAsync();

        // Assert
        Assert.Single(_sender.Sent);
        var dispatch = Assert.Single(_dispatches.Items);
        Assert.Equal(NotificationKind.DayAgenda, dispatch.Kind);
        Assert.Equal(Today, dispatch.LocalDate);
        Assert.Null(dispatch.LessonId);
        Assert.Equal(Utc(Today.AddDays(1), TimeOnly.MinValue), dispatch.ExpiresAtUtc);
    }

    [Fact]
    public async Task RunAsync_DayClosedWithAnUnmarkedLesson_SendsTheSummary()
    {
        // Arrange — the day's only lesson ended at 17:00 local and was never marked.
        var studentId = AddStudent();
        AddProfile(remind: null, daySummary: true);
        AddLesson(studentId, new TimeOnly(16, 0));

        // Act
        await Build(Utc(Today, new TimeOnly(17, 15))).RunAsync();

        // Assert
        var dispatch = Assert.Single(_dispatches.Items);
        Assert.Equal(NotificationKind.DaySummary, dispatch.Kind);
        Assert.Equal(Today, dispatch.LocalDate);
    }

    [Fact]
    public async Task RunAsync_DayWithNothingUnmarked_SendsNoSummary()
    {
        // Arrange — a fully marked day reaches its closed form by re-rendering, never by a fresh send.
        var studentId = AddStudent();
        AddProfile(remind: null, daySummary: true);
        AddLesson(studentId, new TimeOnly(16, 0), status: LessonStatus.Completed);

        // Act
        await Build(Utc(Today, new TimeOnly(17, 15))).RunAsync();

        // Assert
        Assert.Empty(_sender.Sent);
        Assert.Empty(_dispatches.Items);
    }

    [Fact]
    public async Task RunAsync_LessonWithALiveDispatch_SendsNothingForIt()
    {
        // Arrange
        var studentId = AddStudent();
        AddProfile(remind: 30);
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var now = Utc(Today, new TimeOnly(15, 45));

        // Act — two ticks with nothing changing in between.
        await Build(now).RunAsync();
        await Build(now).RunAsync();

        // Assert — the live dispatch IS the dedup, and the identical re-render costs no edit either.
        Assert.Single(_sender.Sent);
        Assert.Empty(_sender.Edited);
        Assert.Single(_dispatches.Items);
        Assert.Equal(lesson.Id, _dispatches.Items[0].LessonId);
    }

    [Fact]
    public async Task RunAsync_LessonRescheduledAfterItsReminder_RetiresTheOldMessageAndArmsANewOne()
    {
        // Arrange
        var studentId = AddStudent();
        AddProfile(remind: 30);
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Act — the lesson moves an hour later and the next tick lands inside its new lead window.
        lesson.Reschedule(Utc(Today, new TimeOnly(17, 0)));
        await Build(Utc(Today, new TimeOnly(16, 40))).RunAsync();

        // Assert
        // Reconciliation runs BEFORE planning, so the retracted row frees a brand-new reminder in the
        // very same tick — this is what the partial unique index on live rows exists for.
        Assert.Empty(Assert.Single(_sender.Edited).Rows);
        Assert.Equal(2, _sender.Sent.Count);
        Assert.Equal(2, _dispatches.Items.Count);
        Assert.False(_dispatches.Items[0].IsLive);
        Assert.True(_dispatches.Items[1].IsLive);
    }

    [Fact]
    public async Task RunAsync_SendReturnsUnreachable_DisablesTheBotAndAbandonsTheTutor()
    {
        // Arrange — two lessons both inside the 30-minute lead window → two due reminders.
        var studentId = AddStudent();
        var profile = AddProfile(remind: 30);
        AddLesson(studentId, new TimeOnly(16, 0));
        AddLesson(studentId, new TimeOnly(16, 10));
        _sender.Result = TelegramSendResult.Unreachable;

        // Act
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Assert
        // The 403 flips reachability off, records no dispatch (so the reminder can still fire once the
        // bot is re-enabled in-window) and stops the tutor's queue after the first attempt.
        Assert.False(profile.BotReachable);
        Assert.Empty(_dispatches.Items);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task RunAsync_TutorBotUnreachable_SkipsTutorEntirely()
    {
        // Arrange
        var studentId = AddStudent();
        var profile = AddProfile(remind: 30);
        profile.MarkBotUnreachable();
        AddLesson(studentId, new TimeOnly(16, 0));

        // Act
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Assert — an unreachable profile is neither notifiable nor worth reconciling.
        Assert.Empty(_sender.Sent);
        Assert.Empty(_dispatches.Items);
    }

    [Fact]
    public async Task RunAsync_SeveralNotifiableTutors_EstablishesEachTutorAsTheTenantInTurn()
    {
        // Arrange — the tick itself is tenant-less: it reads the notifiable profiles across all tutors,
        // then works one tenant at a time.
        var mine = AddStudent();
        var theirs = AddStudent("Zoe", OtherTutor);
        AddProfile(remind: 30);
        AddProfile(remind: 30, tutorId: OtherTutor);
        AddLesson(mine, new TimeOnly(16, 0));
        AddLesson(theirs, new TimeOnly(16, 0), OtherTutor);

        // Act
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Assert — each dispatch is stamped with the tutor whose scope wrote it.
        Assert.Equal(new[] { Tutor, OtherTutor }, _tenant.Tenants);
        Assert.Equal(new[] { Tutor, OtherTutor }, _sender.Sent.Select(s => s.ChatId));
        Assert.Equal(new[] { Tutor, OtherTutor }, _dispatches.Items.Select(d => d.TutorTelegramId));
    }

    [Fact]
    public async Task RunAsync_TutorWithEveryOptInOffButALiveDispatch_IsStillReconciled()
    {
        // Arrange — the tutor turned everything off after their reminder went out. Nobody would ever
        // retire that keyboard if the tick only walked the notifiable profiles.
        var studentId = AddStudent();
        var profile = AddProfile(remind: 30);
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        profile.UpdateRemindMinutes(null);
        profile.UpdateDaySummary(false);
        profile.UpdateMorningAgenda(false);
        Assert.False(profile.WantsAnyNotification);

        // Act — the lesson has ended, so the live row owes one final pass.
        await Build(Utc(Today, new TimeOnly(17, 30))).RunAsync();

        // Assert
        Assert.Empty(Assert.Single(_sender.Edited).Rows);
        Assert.False(Assert.Single(_dispatches.Items).IsLive);
        Assert.Equal(lesson.Id, _dispatches.Items[0].LessonId);
    }

    [Fact]
    public async Task RunAsync_CallBudgetExhausted_StopsTheTickWithoutTouchingTheNextTutor()
    {
        // Arrange — one call in the whole tick and two tutors with a due reminder each.
        _options = new NotificationsOptions { MaxTelegramCallsPerTick = 1 };
        var mine = AddStudent();
        var theirs = AddStudent("Zoe", OtherTutor);
        AddProfile(remind: 30);
        AddProfile(remind: 30, tutorId: OtherTutor);
        AddLesson(mine, new TimeOnly(16, 0));
        AddLesson(theirs, new TimeOnly(16, 0), OtherTutor);

        // Act
        await Build(Utc(Today, new TimeOnly(15, 45))).RunAsync();

        // Assert — the second tutor is picked up by the next tick instead of tripping the rate limit.
        Assert.Equal(Tutor, Assert.Single(_sender.Sent).ChatId);
        Assert.Single(_dispatches.Items);
    }

    [Fact]
    public async Task RunAsync_DayEndingAfterLocalMidnight_SummarisesTheDayItBeganOn()
    {
        // Arrange — a 23:30–00:30 lesson holds its day open, so the summary waits past local midnight.
        var studentId = AddStudent();
        AddProfile(remind: null, daySummary: true);
        AddLesson(studentId, new TimeOnly(23, 30));
        var now = Utc(Today.AddDays(1), new TimeOnly(0, 45));

        // Act — 00:45 the next local day: 15 minutes of grace past the 00:30 end.
        await Build(now).RunAsync();

        // Assert
        var dispatch = Assert.Single(_dispatches.Items);
        Assert.Equal(NotificationKind.DaySummary, dispatch.Kind);
        Assert.Equal(Today, dispatch.LocalDate);
        // The expiry has to lie AHEAD of the send, or reconciliation would retire it on the next tick.
        Assert.True(dispatch.ExpiresAtUtc > now);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
