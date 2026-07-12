using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Features.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationRunnerTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday; London is on BST (UTC+1) in July, so a 16:00 local slot is 15:00 UTC.
    private static readonly DateOnly Monday = new(2026, 7, 6);

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeStudentRepository _students = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeTutorProfileRepository _profiles = new();
    private readonly FakeNotificationSender _sender = new();

    private NotificationRunner Build(DateTimeOffset now)
    {
        var reader = new ScheduleReader(_lessons, new SeriesExpansion(_lessons, _series), _students);
        var materializer = new LessonMaterializer(_students, TimeProvider.System, NullLogger<LessonMaterializer>.Instance);
        return new NotificationRunner(
            _profiles, reader, _lessons, _series, materializer, _students,
            _sender, new NotificationPlanner(), new NotificationText(), _uow,
            new FixedClock(now), Options.Create(new NotificationsOptions()),
            NullLogger<NotificationRunner>.Instance);
    }

    private TutorProfile AddProfile(int? remind, bool followUp)
    {
        var profile = TutorProfile.Create(Tutor, London, CreatedAt).Value;
        profile.UpdateRemindMinutes(remind);
        profile.UpdateNotifyAfterLesson(followUp);
        _profiles.Items.Add(profile);
        return profile;
    }

    private Guid AddStudent(string name)
    {
        var student = Student.Create(Tutor, name, 100m, CreatedAt).Value;
        _students.Items.Add(student);
        return student.Id;
    }

    [Fact]
    public async Task RunAsync_DueReminderOnVirtualSlot_MaterializesPersistsAndSends()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        AddProfile(remind: 30, followUp: false);
        _series.Items.Add(LessonSeries.Create(
            Tutor, studentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt).Value);

        // Slot starts 15:00 UTC; 14:45 is 15 min before → inside the 30-min reminder window.
        var now = new DateTimeOffset(2026, 7, 6, 14, 45, 0, TimeSpan.Zero);

        // Act
        await Build(now).RunAsync();

        // Assert
        // The virtual slot is materialized AND persisted before the send, then flagged after delivery.
        var lesson = Assert.Single(_lessons.Items);
        Assert.Equal(now, lesson.Notifications.ReminderSentAtUtc);

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(Tutor, sent.ChatId);
    }

    [Fact]
    public async Task RunAsync_DueReminder_SendsSingleCancelButtonWithLessonCallback()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        AddProfile(remind: 30, followUp: false);
        _series.Items.Add(LessonSeries.Create(
            Tutor, studentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt).Value);

        // Slot starts 15:00 UTC; 14:45 is 15 min before → inside the 30-min reminder window.
        var now = new DateTimeOffset(2026, 7, 6, 14, 45, 0, TimeSpan.Zero);

        // Act
        await Build(now).RunAsync();

        // Assert
        // The reminder carries exactly one Cancel button on the shared 'x:' cancel callback (same as the
        // follow-up's ❌, so both obey the single Completed-status guard), whose payload references the
        // now-persisted lesson id. The profile has no language set, so Ukrainian (the default) label is used.
        var lesson = Assert.Single(_lessons.Items);
        var button = Assert.Single(Assert.Single(_sender.Sent).Buttons);
        Assert.Equal("❌ Скасувати", button.Text);
        Assert.Equal($"x:{lesson.Id:N}", button.CallbackData);
    }

    [Fact]
    public async Task RunAsync_DueFollowUpOnPhysicalLesson_MarksSentWithButtons()
    {
        // Arrange
        var studentId = AddStudent("Bob");
        AddProfile(remind: null, followUp: true);

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        // Ended 30 min ago (start = now - 90, 60-min lesson) → inside the 60-min follow-up lookback.
        var lesson = Lesson.Create(Tutor, studentId, now.AddMinutes(-90), 60, 100m, CreatedAt).Value;
        _lessons.Items.Add(lesson);

        // Act
        await Build(now).RunAsync();

        // Assert
        Assert.Equal(now, lesson.Notifications.FollowUpSentAtUtc);
        Assert.Equal(1, _uow.SaveCount);

        // Three buttons (✅/💰/❌) whose callback data references the persisted lesson id.
        var buttons = Assert.Single(_sender.Sent).Buttons;
        Assert.Equal(3, buttons.Count);
        Assert.All(buttons, b => Assert.Contains(lesson.Id.ToString("N"), b.CallbackData));
    }

    [Fact]
    public async Task RunAsync_TransientReminderOnVirtualSlot_PersistsLessonButLeavesUnmarked()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        AddProfile(remind: 30, followUp: false);
        _series.Items.Add(LessonSeries.Create(
            Tutor, studentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt).Value);
        _sender.Result = TelegramSendResult.TransientFailure;

        // Slot starts 15:00 UTC; 14:45 is 15 min before → inside the 30-min reminder window.
        var now = new DateTimeOffset(2026, 7, 6, 14, 45, 0, TimeSpan.Zero);

        // Act
        await Build(now).RunAsync();

        // Assert
        // The slot was materialized and persisted up-front, so it survives the transient failure —
        // it is present in the repo but left unmarked to be retried against the same id next tick.
        var lesson = Assert.Single(_lessons.Items);
        Assert.False(lesson.Notifications.IsReminderSent);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task RunAsync_RemindMinutesNull_SendsFollowUpOnly()
    {
        // Arrange
        var studentId = AddStudent("Bob");
        AddProfile(remind: null, followUp: true);

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        var lesson = Lesson.Create(Tutor, studentId, now.AddMinutes(-90), 60, 100m, CreatedAt).Value;
        _lessons.Items.Add(lesson);

        // Act
        await Build(now).RunAsync();

        // Assert
        // Exactly one send, and it is the follow-up (three buttons) — no reminder went out.
        Assert.Equal(3, Assert.Single(_sender.Sent).Buttons.Count);
    }

    [Fact]
    public async Task RunAsync_ReminderAlreadyFlagged_FiltersBeforeSending()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        AddProfile(remind: 30, followUp: false);

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        // Upcoming lesson inside the reminder window, but its reminder was already sent.
        var lesson = Lesson.Create(Tutor, studentId, now.AddMinutes(15), 60, 100m, CreatedAt).Value;
        lesson.MarkReminderSent(now.AddMinutes(-5));
        _lessons.Items.Add(lesson);

        // Act
        await Build(now).RunAsync();

        // Assert
        Assert.Empty(_sender.Sent);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task RunAsync_SendReturnsUnreachable_DisablesBotAndStopsTutor()
    {
        // Arrange
        var studentId = AddStudent("Bob");
        var profile = AddProfile(remind: null, followUp: true);
        _sender.Result = TelegramSendResult.Unreachable;

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        // Two physical lessons both ended inside the 60-min follow-up lookback → two due follow-ups.
        var first = Lesson.Create(Tutor, studentId, now.AddMinutes(-90), 60, 100m, CreatedAt).Value;
        var second = Lesson.Create(Tutor, studentId, now.AddMinutes(-80), 60, 100m, CreatedAt).Value;
        _lessons.Items.Add(first);
        _lessons.Items.Add(second);

        // Act
        await Build(now).RunAsync();

        // Assert
        // The 403 flips reachability off, leaves the lesson unmarked (so it can fire once re-enabled),
        // and stops the tutor's queue — only the first due item was attempted.
        Assert.False(profile.BotReachable);
        Assert.False(first.Notifications.IsFollowUpSent);
        Assert.False(second.Notifications.IsFollowUpSent);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task RunAsync_TutorBotUnreachable_SkipsTutorEntirely()
    {
        // Arrange
        var studentId = AddStudent("Bob");
        var profile = AddProfile(remind: null, followUp: true);
        profile.MarkBotUnreachable();

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        var lesson = Lesson.Create(Tutor, studentId, now.AddMinutes(-90), 60, 100m, CreatedAt).Value;
        _lessons.Items.Add(lesson);

        // Act
        await Build(now).RunAsync();

        // Assert
        // GetNotifiableAsync excludes an unreachable profile, so nothing is planned or sent.
        Assert.Empty(_sender.Sent);
        Assert.False(lesson.Notifications.IsFollowUpSent);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
