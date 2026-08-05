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

public class NotificationReconcilerTests
{
    private const long Tutor = 555;
    private const int MessageId = 900;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // Europe/London is on BST (UTC+1) in August, so every local time below is one hour ahead of UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateOnly Today = new(2026, 8, 5);

    private readonly RecordingTutorScope _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeStudentRepository _students;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentDebtReader _debts;
    private readonly FakeNotificationDispatchRepository _dispatches;
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeNotificationSender _sender = new();
    private readonly NotificationRenderer _renderer = new(Options.Create(new NotificationsOptions()));

    public NotificationReconcilerTests()
    {
        _lessons = new FakeLessonRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _debts = new FakeStudentDebtReader(_lessons);
        _dispatches = new FakeNotificationDispatchRepository(_tenant);
        // The runner establishes the tenant before reconciliation runs; the reconciler never touches it.
        _tenant.SetForBackground(Tutor);
    }

    private NotificationViewBuilder Views(DateTimeOffset now) =>
        new(_lessons, _students, _series, _debts, new FixedClock(now));

    private NotificationReconciler Build(DateTimeOffset now) =>
        new(_dispatches, Views(now), _renderer, _sender, _uow, new FixedClock(now),
            Options.Create(new NotificationsOptions()), NullLogger<NotificationReconciler>.Instance);

    private static TutorProfile Profile()
    {
        var profile = TutorProfile.Create(Tutor, London, CreatedAt).Value;
        profile.UpdateRemindMinutes(30);
        return profile;
    }

    private static TelegramCallBudget Budget(int max = 100) => new(max);

    private static DateTimeOffset Utc(DateOnly localDate, TimeOnly localTime) =>
        WallClock.ToUtc(localDate, localTime, London);

    private Guid AddStudent(string name = "Ann")
    {
        var student = Student.Create(name, 100m, CreatedAt).Value.OwnedBy(Tutor);
        _students.Items.Add(student);
        return student.Id;
    }

    private Lesson AddLesson(
        Guid studentId, TimeOnly localStart, decimal price = 500m, LessonStatus status = LessonStatus.Scheduled)
    {
        var lesson = Lesson.Create(studentId, Utc(Today, localStart), 60, price, CreatedAt).Value.OwnedBy(Tutor);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    /// <summary>
    /// The dispatch a first send would have written: the message is on screen with the snapshot and hash
    /// of exactly what was rendered at <paramref name="sentAt"/>.
    /// </summary>
    private async Task<NotificationDispatch> ArmReminderAsync(
        Lesson lesson, DateTimeOffset sentAt, int? messageId = MessageId)
    {
        var view = await Views(sentAt).BuildReminderAsync(
            lesson.Id, Profile(), previous: null, sentAt, CancellationToken.None);
        var message = _renderer.Reminder(view!);
        var dispatch = NotificationDispatch.ForReminder(
                lesson.Id, Tutor, messageId, message.Snapshot, message.ContentHash, lesson.EndUtc, sentAt)
            .OwnedBy(Tutor);
        _dispatches.Items.Add(dispatch);
        return dispatch;
    }

    private async Task<NotificationDispatch> ArmAgendaAsync(DateTimeOffset sentAt, DateTimeOffset expiresAt)
    {
        var view = await Views(sentAt).BuildAgendaAsync(Today, Profile(), previous: null, sentAt, CancellationToken.None);
        var message = _renderer.Agenda(view);
        var dispatch = NotificationDispatch.ForDay(
                NotificationKind.DayAgenda, Today, Tutor, MessageId,
                message.Snapshot, message.ContentHash, expiresAt, sentAt)
            .OwnedBy(Tutor);
        _dispatches.Items.Add(dispatch);
        return dispatch;
    }

    private async Task<NotificationDispatch> ArmSummaryAsync(DateTimeOffset sentAt, DateTimeOffset expiresAt)
    {
        var view = await Views(sentAt).BuildSummaryAsync(
            Today, Profile(), focusedLessonId: null, expanded: false, sentAt, CancellationToken.None);
        var message = _renderer.Summary(view);
        var dispatch = NotificationDispatch.ForDay(
                NotificationKind.DaySummary, Today, Tutor, MessageId,
                message.Snapshot, message.ContentHash, expiresAt, sentAt)
            .OwnedBy(Tutor);
        _dispatches.Items.Add(dispatch);
        return dispatch;
    }

    [Fact]
    public async Task ReconcileAsync_LessonRescheduled_EditsTheOldMessageRetractsItAndFreesANewReminder()
    {
        // Arrange
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var armedAt = Utc(Today, new TimeOnly(15, 45));
        var dispatch = await ArmReminderAsync(lesson, armedAt);
        lesson.Reschedule(Utc(Today, new TimeOnly(18, 0)));
        var now = Utc(Today, new TimeOnly(15, 46));

        // Act
        await Build(now).ReconcileAsync(Profile(), Budget());

        // Assert
        // The old message settles into "перенесено" with no keyboard left and the row retires, which is
        // exactly what lets the planner arm a brand-new reminder for the new slot.
        var edited = Assert.Single(_sender.Edited);
        Assert.Equal(MessageId, edited.MessageId);
        Assert.Empty(edited.Rows);
        Assert.False(dispatch.IsLive);

        var live = await _dispatches.GetLiveAsync();
        var due = new NotificationPlanner().Plan(
            Profile(), [], [lesson], live, Utc(Today, new TimeOnly(17, 45)), 15);
        Assert.Equal(lesson.Id, Assert.Single(due).LessonId);
    }

    [Fact]
    public async Task ReconcileAsync_LessonCancelled_EditsIntoTerminalFormWithNoKeyboard()
    {
        // Arrange
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)));
        lesson.ChangeStatus(LessonStatus.Cancelled);

        // Act
        await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(Profile(), Budget());

        // Assert
        var edited = Assert.Single(_sender.Edited);
        Assert.Empty(edited.Rows);
        Assert.False(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_LessonDeletedWithASnapshot_EditsIntoRemovedFormAndRetracts()
    {
        // Arrange — no foreign key ties the dispatch to the lesson, so a deleted lesson still renders.
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)));
        _lessons.Items.Remove(lesson);

        // Act
        await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Empty(Assert.Single(_sender.Edited).Rows);
        Assert.False(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_ClockCrossesLessonStart_EditsIntoTheStartedForm()
    {
        // Arrange — nothing about the lesson changes; only the clock moves past its start.
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)));
        var armedHash = dispatch.ContentHash;

        // Act
        await Build(Utc(Today, new TimeOnly(16, 0))).ReconcileAsync(Profile(), Budget());

        // Assert
        // The phase is part of the rendered text and therefore part of the hash, so the repaint needs no
        // timer of its own. The message stays live with its "проведено" keyboard.
        var edited = Assert.Single(_sender.Edited);
        var callbacks = edited.Rows.SelectMany(r => r.Buttons).Select(b => b.CallbackData).ToList();
        Assert.Equal(new[] { $"c:{lesson.Id:N}", $"p:{lesson.Id:N}" }, callbacks);
        Assert.True(dispatch.IsLive);
        Assert.NotEqual(armedHash, dispatch.ContentHash);
    }

    [Fact]
    public async Task ReconcileAsync_NothingChanged_SpendsNoTelegramCall()
    {
        // Arrange
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var armedAt = Utc(Today, new TimeOnly(15, 45));
        var dispatch = await ArmReminderAsync(lesson, armedAt);

        // Act
        var calls = await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(Profile(), Budget());

        // Assert — an identical re-render is the whole point of storing the hash.
        Assert.Equal(0, calls);
        Assert.Empty(_sender.Edited);
        Assert.True(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_ExpiredDispatch_RunsExactlyOneFinalPassAndRetracts()
    {
        // Arrange — the lesson ended, so the reminder owes one last edit and nothing after that.
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)));
        var now = Utc(Today, new TimeOnly(17, 30));

        // Act
        await Build(now).ReconcileAsync(Profile(), Budget());
        await Build(now.AddMinutes(1)).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Empty(Assert.Single(_sender.Edited).Rows);
        Assert.False(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_SeededRowWithNoMessageId_IsNeverEdited()
    {
        // Arrange — a row the migration seeded has no message to rewrite, but still blocks a duplicate.
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)), messageId: null);

        // Act
        await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Empty(_sender.Edited);
        Assert.True(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_ExpiredSeededRowWithNoMessageId_IsRetractedWithoutAnEdit()
    {
        // Arrange
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)), messageId: null);

        // Act
        await Build(Utc(Today, new TimeOnly(17, 30))).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Empty(_sender.Edited);
        Assert.False(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_AllOfDaysLessonsDeleted_RetractsTheAgendaRowWithoutRerendering()
    {
        // Arrange — the agenda was sent for a day that later loses every lesson to deletion (not
        // cancellation): GetInRangeAsync no longer returns any row for it, so the rebuilt view has zero
        // lines and there is nothing left to render — mirrors the reminder's "gone with no snapshot" null
        // path instead of falling into Agenda()'s multi-lesson branch with an empty list.
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(9, 0));
        var sentAt = Utc(Today, new TimeOnly(7, 0));
        var dispatch = await ArmAgendaAsync(sentAt, Utc(Today.AddDays(1), TimeOnly.MinValue));
        _lessons.Items.Remove(lesson);

        // Act
        await Build(sentAt.AddMinutes(1)).ReconcileAsync(Profile(), Budget());

        // Assert — retracted straight away: no Telegram call, and no exception that would have skipped
        // the rest of this tutor's queue and left the row live forever.
        Assert.Empty(_sender.Edited);
        Assert.False(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_LessonMarkedInTheApp_DropsOutOfTheEveningList()
    {
        // Arrange — two unmarked lessons in the summary; one is then completed from the Mini App.
        var studentId = AddStudent();
        var marked = AddLesson(studentId, new TimeOnly(16, 0));
        var stillOpen = AddLesson(studentId, new TimeOnly(17, 0));
        var sentAt = Utc(Today, new TimeOnly(18, 15));
        var dispatch = await ArmSummaryAsync(sentAt, Utc(Today.AddDays(1), TimeOnly.MinValue));
        marked.ChangeStatus(LessonStatus.Completed);

        // Act
        await Build(sentAt.AddMinutes(1)).ReconcileAsync(Profile(), Budget());

        // Assert
        // The marked lesson stops being a button (the body carries its ✅ instead); the other stays.
        var edited = Assert.Single(_sender.Edited);
        var callbacks = edited.Rows.SelectMany(r => r.Buttons).Select(b => b.CallbackData).ToList();
        Assert.DoesNotContain($"s:{marked.Id:N}", callbacks);
        Assert.Contains($"s:{stillOpen.Id:N}", callbacks);
        Assert.True(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_SummaryResyncedWithinPollInterval_SkipsRepaint()
    {
        // Arrange — the webhook just re-rendered this row (e.g. into its S2 focus form); s:/b:/m: never
        // persist that navigation state, so an immediate reconciliation pass must not snap the tutor's
        // screen back to the plain list before they can act on what they tapped into.
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var syncedAt = Utc(Today, new TimeOnly(18, 15));
        var dispatch = await ArmSummaryAsync(syncedAt, Utc(Today.AddDays(1), TimeOnly.MinValue));
        lesson.ChangeStatus(LessonStatus.Completed); // would otherwise force a repaint

        // Act — well within the default one-minute poll interval.
        await Build(syncedAt.AddSeconds(30)).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Empty(_sender.Edited);
        Assert.True(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_ExpiredSummaryWithinPollInterval_StillRunsFinalPass()
    {
        // Arrange — an expired row can no longer be mid-navigation, so it must not be shielded.
        var studentId = AddStudent();
        AddLesson(studentId, new TimeOnly(16, 0), status: LessonStatus.Completed);
        var syncedAt = Utc(Today, new TimeOnly(18, 15));
        var dispatch = await ArmSummaryAsync(syncedAt, expiresAt: syncedAt);

        // Act
        await Build(syncedAt.AddSeconds(30)).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Single(_sender.Edited);
        Assert.False(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_EditReturnsPermanentFailure_RetractsTheRow()
    {
        // Arrange — the message is gone; there is nothing left to keep truthful.
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)));
        lesson.ChangeStatus(LessonStatus.Cancelled);
        _sender.EditResult = TelegramSendResult.PermanentFailure;

        // Act
        await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Single(_sender.Edited);
        Assert.False(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_EditReturnsTransientFailure_LeavesTheRowUntouched()
    {
        // Arrange
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)));
        var armedHash = dispatch.ContentHash;
        lesson.ChangeStatus(LessonStatus.Cancelled);
        _sender.EditResult = TelegramSendResult.TransientFailure;

        // Act
        await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(Profile(), Budget());

        // Assert — the next tick re-renders and retries against the very same message.
        Assert.True(dispatch.IsLive);
        Assert.Equal(armedHash, dispatch.ContentHash);
    }

    [Fact]
    public async Task ReconcileAsync_EditReturnsUnreachable_MarksTheBotUnreachableAndStops()
    {
        // Arrange — two live reminders, both changed; the first edit comes back 403.
        var studentId = AddStudent();
        var first = AddLesson(studentId, new TimeOnly(16, 0));
        var second = AddLesson(studentId, new TimeOnly(17, 0));
        await ArmReminderAsync(first, Utc(Today, new TimeOnly(15, 45)));
        await ArmReminderAsync(second, Utc(Today, new TimeOnly(15, 45)));
        first.ChangeStatus(LessonStatus.Cancelled);
        second.ChangeStatus(LessonStatus.Cancelled);
        _sender.EditResult = TelegramSendResult.Unreachable;
        var profile = Profile();

        // Act
        await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(profile, Budget());

        // Assert — the caller sees the flag flip and drops this tutor for the tick.
        Assert.False(profile.BotReachable);
        Assert.Single(_sender.Edited);
    }

    [Fact]
    public async Task ReconcileAsync_BudgetExhausted_IssuesNoEdit()
    {
        // Arrange
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var dispatch = await ArmReminderAsync(lesson, Utc(Today, new TimeOnly(15, 45)));
        lesson.ChangeStatus(LessonStatus.Cancelled);

        // Act
        var calls = await Build(Utc(Today, new TimeOnly(15, 46))).ReconcileAsync(Profile(), Budget(max: 0));

        // Assert — the backlog is drained on a later tick instead of tripping Telegram's rate limits.
        Assert.Equal(0, calls);
        Assert.Empty(_sender.Edited);
        Assert.True(dispatch.IsLive);
    }

    [Fact]
    public async Task ReconcileAsync_AnotherTutorsLiveDispatch_IsNeverTouched()
    {
        // Arrange — the fail-closed query filter is what keeps one tenant out of another's messages.
        const long otherTutor = 777;
        var studentId = AddStudent();
        var lesson = AddLesson(studentId, new TimeOnly(16, 0));
        var theirs = NotificationDispatch
            .ForReminder(lesson.Id, otherTutor, 42, RenderedSnapshot.Empty, "", lesson.EndUtc, CreatedAt)
            .OwnedBy(otherTutor);
        _dispatches.Items.Add(theirs);

        // Act
        await Build(Utc(Today, new TimeOnly(17, 30))).ReconcileAsync(Profile(), Budget());

        // Assert
        Assert.Empty(_sender.Edited);
        Assert.True(theirs.IsLive);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
