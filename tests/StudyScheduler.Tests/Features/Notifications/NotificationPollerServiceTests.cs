using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Core.Tenancy;
using StudyScheduler.Tests.Features.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationPollerServiceTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunTickAsync_ResolvesScopedRunner_InvokesRunOnce()
    {
        // Arrange
        // A due follow-up on a physical lesson is the observable signal that the runner actually ran:
        // if RunTickAsync resolves the runner from the scope and awaits it, the fake sender records one send.
        // The scope starts tenant-less, exactly as the poller's does — the tick takes its tenant from
        // each notifiable profile.
        var tenant = new TutorContext();
        var lessons = new FakeLessonRepository(tenant);
        var students = new FakeStudentRepository(tenant);
        var uow = new FakeUnitOfWork();
        var profiles = new FakeTutorProfileRepository(tenant);
        var sender = new FakeNotificationSender();

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        var student = Student.Create("Bob", 100m, CreatedAt).Value.OwnedBy(Tutor);
        students.Items.Add(student);
        var profile = TutorProfile.Create(Tutor, TimeZoneInfo.Utc, CreatedAt).Value;
        profile.UpdateRemindMinutes(null);
        profile.UpdateNotifyAfterLesson(true);
        profiles.Items.Add(profile);
        lessons.Items.Add(Lesson.Create(student.Id, now.AddMinutes(-90), 60, 100m, CreatedAt).Value.OwnedBy(Tutor));

        var runner = new NotificationRunner(
            profiles, lessons, students, sender, new NotificationPlanner(), new NotificationText(), uow,
            tenant, new FixedClock(now), Options.Create(new NotificationsOptions()),
            NullLogger<NotificationRunner>.Instance);

        var services = new ServiceCollection();
        services.AddScoped(_ => runner);
        var provider = services.BuildServiceProvider();

        var poller = new NotificationPollerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NotificationsOptions()),
            NullLogger<NotificationPollerService>.Instance);

        // Act
        await poller.RunTickAsync(CancellationToken.None);

        // Assert
        Assert.Single(sender.Sent);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
