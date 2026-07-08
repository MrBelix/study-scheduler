using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class LessonNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeTutorProfileRepository Profiles { get; } = new();
        public FakeStudentRepository Students { get; } = new();
        public FakeLessonNotificationRepository SentLog { get; } = new();
        public FakeUnitOfWork Uow { get; } = new();
        public FakeLessonRepository Lessons { get; } = new();
        public FakeLessonSeriesRepository Series { get; } = new();
        public FakeTelegramBotClient Bot { get; } = new();

        public LessonNotifier CreateNotifier() => new(
            Profiles,
            Students,
            SentLog,
            Uow,
            new LessonMaterializer(
                Lessons,
                new SeriesExpansion(Lessons, Series),
                Students,
                new FixedTimeProvider(Now),
                NullLogger<LessonMaterializer>.Instance),
            Bot,
            Options.Create(new NotificationsOptions()),
            new FixedTimeProvider(Now),
            NullLogger<LessonNotifier>.Instance);

        public TutorProfile AddProfile(long tutorId)
        {
            // Defaults: RemindMinutes = 30, NotifyAfterLesson = true.
            var profile = TutorProfile.Create(tutorId, TimeZoneInfo.Utc, Now.AddDays(-10)).Value;
            Profiles.Profiles.Add(profile);
            return profile;
        }

        public Lesson AddLesson(long tutorId, DateTimeOffset startUtc)
        {
            var student = Student.Create(tutorId, "Alice", 100m, Now.AddDays(-10)).Value;
            Students.Students.Add(student);
            var lesson = Lesson.Create(tutorId, student.Id, startUtc, 60, 100m, Now.AddDays(-1)).Value;
            Lessons.Lessons.Add(lesson);
            return lesson;
        }
    }

    [Fact]
    public async Task Sends_only_notifications_missing_from_dedup_log()
    {
        var harness = new Harness();
        harness.AddProfile(555);
        var alreadyNotified = harness.AddLesson(555, Now.AddMinutes(-70)); // ended 10 min ago
        var fresh = harness.AddLesson(555, Now.AddMinutes(-65));           // ended 5 min ago
        harness.SentLog.AlreadySent.Add($"L:{alreadyNotified.Id}");

        await harness.CreateNotifier().RunOnceAsync(CancellationToken.None);

        Assert.Equal(555, Assert.Single(harness.Bot.SentMessages).ChatId);
        var recorded = Assert.Single(harness.SentLog.Added);
        Assert.Equal($"L:{fresh.Id}", recorded.SlotKey);
        Assert.Equal(LessonNotificationKind.FollowUp, recorded.Kind);
    }

    [Fact]
    public async Task Failed_send_is_not_recorded_so_the_next_tick_retries()
    {
        var harness = new Harness();
        harness.AddProfile(555);
        harness.AddLesson(555, Now.AddMinutes(-70));
        harness.Bot.SendResult = false;

        var notifier = harness.CreateNotifier();
        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Single(harness.Bot.SentMessages);
        Assert.Empty(harness.SentLog.Added);
        Assert.Equal(0, harness.Uow.SaveCount);

        // Nothing was recorded, so the next tick plans and delivers the same notification.
        harness.Bot.SendResult = true;
        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, harness.Bot.SentMessages.Count);
        Assert.Single(harness.SentLog.Added);
    }

    [Fact]
    public async Task Successful_send_records_exactly_one_dedup_row_and_commits()
    {
        var harness = new Harness();
        harness.AddProfile(555);
        var upcoming = harness.AddLesson(555, Now.AddMinutes(20)); // reminder due (lead 30 min)

        await harness.CreateNotifier().RunOnceAsync(CancellationToken.None);

        Assert.Single(harness.Bot.SentMessages);
        var recorded = Assert.Single(harness.SentLog.Added);
        Assert.Equal(LessonNotificationKind.Reminder, recorded.Kind);
        Assert.Equal($"L:{upcoming.Id}", recorded.SlotKey);
        Assert.Equal(1, harness.Uow.SaveCount);
    }

    [Fact]
    public async Task One_tutors_failure_discards_staged_changes_and_does_not_starve_other_tutors()
    {
        var harness = new Harness();
        harness.AddProfile(111);
        harness.AddProfile(222);
        harness.AddLesson(111, Now.AddMinutes(-70));
        harness.AddLesson(222, Now.AddMinutes(-70));
        harness.SentLog.ThrowForTutor = 111;

        await harness.CreateNotifier().RunOnceAsync(CancellationToken.None);

        // Tutor 111 blew up before sending; tutor 222 still got its follow-up.
        Assert.Equal(222, Assert.Single(harness.Bot.SentMessages).ChatId);
        Assert.Equal(1, harness.Uow.DiscardCount);
        Assert.Equal(222, Assert.Single(harness.SentLog.Added).TutorTelegramId);
    }
}
