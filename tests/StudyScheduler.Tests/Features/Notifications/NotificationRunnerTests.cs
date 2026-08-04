using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Core.Tenancy;
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

    private readonly RecordingTutorScope _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeStudentRepository _students;
    private readonly FakeTutorProfileRepository _profiles;
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeNotificationSender _sender = new();

    public NotificationRunnerTests()
    {
        // The tick starts tenant-less and borrows each notifiable tutor's tenant in turn, so the
        // repositories read through the very scope the runner drives.
        _lessons = new FakeLessonRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _profiles = new FakeTutorProfileRepository(_tenant);
    }

    private NotificationRunner Build(DateTimeOffset now) =>
        new(
            _profiles, _lessons, _students, _sender, new NotificationPlanner(), new NotificationText(),
            _uow, _tenant, new FixedClock(now), Options.Create(new NotificationsOptions()),
            NullLogger<NotificationRunner>.Instance);

    private TutorProfile AddProfile(int? remind, bool followUp, long tutorId = Tutor)
    {
        var profile = TutorProfile.Create(tutorId, London, CreatedAt).Value;
        profile.UpdateRemindMinutes(remind);
        profile.UpdateNotifyAfterLesson(followUp);
        _profiles.Items.Add(profile);
        return profile;
    }

    private Guid AddStudent(string name, long tutorId = Tutor)
    {
        // The pass has no tenant yet, so fixture rows are stamped the way persistence stamps them.
        var student = Student.Create(name, 100m, CreatedAt).Value.OwnedBy(tutorId);
        _students.Items.Add(student);
        return student.Id;
    }

    /// <summary>Monday's lesson of a weekly series, exactly as the generator writes it: 15:00 UTC.</summary>
    private Lesson AddSeriesLesson(Guid studentId, long tutorId = Tutor)
    {
        var series = LessonSeries.Create(
            studentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt).Value;

        var occurrence = series.GetOccurrences(Monday, Monday)[0];
        var lesson = Lesson.Create(
            studentId, occurrence.StartUtc, 60, 100m, CreatedAt,
            seriesId: series.Id, occurrenceDate: Monday).Value.OwnedBy(tutorId);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    /// <summary>A one-off lesson of the given tutor, already owned as a stored row is.</summary>
    private Lesson AddLesson(Guid studentId, DateTimeOffset startUtc, long tutorId = Tutor)
    {
        var lesson = Lesson.Create(studentId, startUtc, 60, 100m, CreatedAt).Value.OwnedBy(tutorId);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    [Fact]
    public async Task RunAsync_DueReminderOnSeriesLesson_SendsAndFlagsTheRow()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        AddProfile(remind: 30, followUp: false);
        var lesson = AddSeriesLesson(studentId);

        // The lesson starts 15:00 UTC; 14:45 is 15 min before → inside the 30-min reminder window.
        var now = new DateTimeOffset(2026, 7, 6, 14, 45, 0, TimeSpan.Zero);

        // Act
        await Build(now).RunAsync();

        // Assert
        // A series lesson is a row like any other: it is sent for and flagged, nothing is created.
        Assert.Same(lesson, Assert.Single(_lessons.Items));
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
        var lesson = AddSeriesLesson(studentId);

        // The lesson starts 15:00 UTC; 14:45 is 15 min before → inside the 30-min reminder window.
        var now = new DateTimeOffset(2026, 7, 6, 14, 45, 0, TimeSpan.Zero);

        // Act
        await Build(now).RunAsync();

        // Assert
        // The reminder carries exactly one Cancel button on the shared 'x:' cancel callback (same as the
        // follow-up's ❌, so both obey the single Completed-status guard), whose payload references the
        // lesson's row id. The profile has no language set, so Ukrainian (the default) label is used.
        var button = Assert.Single(Assert.Single(_sender.Sent).Buttons);
        Assert.Equal("❌ Скасувати", button.Text);
        Assert.Equal($"x:{lesson.Id:N}", button.CallbackData);
    }

    [Fact]
    public async Task RunAsync_DueFollowUpOnOneOffLesson_MarksSentWithButtons()
    {
        // Arrange
        var studentId = AddStudent("Bob");
        AddProfile(remind: null, followUp: true);

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        // Ended 30 min ago (start = now - 90, 60-min lesson) → inside the 60-min follow-up lookback.
        var lesson = AddLesson(studentId, now.AddMinutes(-90));

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
    public async Task RunAsync_TransientReminder_LeavesTheRowUnmarked()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        AddProfile(remind: 30, followUp: false);
        var lesson = AddSeriesLesson(studentId);
        _sender.Result = TelegramSendResult.TransientFailure;

        // The lesson starts 15:00 UTC; 14:45 is 15 min before → inside the 30-min reminder window.
        var now = new DateTimeOffset(2026, 7, 6, 14, 45, 0, TimeSpan.Zero);

        // Act
        await Build(now).RunAsync();

        // Assert
        // Nothing is flagged, so the very same row is retried against the same id next tick.
        Assert.False(lesson.Notifications.IsReminderSent);
        Assert.Equal(0, _uow.SaveCount);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task RunAsync_RemindMinutesNull_SendsFollowUpOnly()
    {
        // Arrange
        var studentId = AddStudent("Bob");
        AddProfile(remind: null, followUp: true);

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        AddLesson(studentId, now.AddMinutes(-90));

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
        AddLesson(studentId, now.AddMinutes(15)).MarkReminderSent(now.AddMinutes(-5));

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
        var first = AddLesson(studentId, now.AddMinutes(-90));
        var second = AddLesson(studentId, now.AddMinutes(-80));

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
        var lesson = AddLesson(studentId, now.AddMinutes(-90));

        // Act
        await Build(now).RunAsync();

        // Assert
        // GetNotifiableAcrossAllTutorsAsync excludes an unreachable profile, so nothing is planned or sent.
        Assert.Empty(_sender.Sent);
        Assert.False(lesson.Notifications.IsFollowUpSent);
    }

    [Fact]
    public async Task RunAsync_NotifiableTutorsOfSeveralTenants_EstablishesEachTutorAsTheTenantInTurn()
    {
        // Arrange
        // The tick itself is tenant-less: it reads the notifiable profiles across all tutors, then
        // works one tenant at a time.
        const long otherTutor = 777;
        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        var mine = AddStudent("Bob");
        var theirs = AddStudent("Zoe", otherTutor);
        AddProfile(remind: null, followUp: true);
        AddProfile(remind: null, followUp: true, tutorId: otherTutor);
        AddLesson(mine, now.AddMinutes(-90));
        AddLesson(theirs, now.AddMinutes(-90), otherTutor);

        // Act
        await Build(now).RunAsync();

        // Assert
        // Both tutors were served, each under its own tenant — never one tenant for the whole tick.
        Assert.Equal(new[] { Tutor, otherTutor }, _tenant.Tenants);
        Assert.Equal(new[] { Tutor, otherTutor }, _sender.Sent.Select(s => s.ChatId));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
