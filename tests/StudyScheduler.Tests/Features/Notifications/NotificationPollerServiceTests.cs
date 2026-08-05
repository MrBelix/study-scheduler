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
using StudyScheduler.Tests.Features.Reports;
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
        // A due reminder on a physical lesson is the observable signal that the runner actually ran:
        // if RunTickAsync resolves the runner from the scope and awaits it, the fake sender records one
        // send. The scope starts tenant-less, exactly as the poller's does — the tick takes its tenant
        // from each notifiable profile.
        var tenant = new TutorContext();
        var lessons = new FakeLessonRepository(tenant);
        var students = new FakeStudentRepository(tenant);
        var series = new FakeLessonSeriesRepository(tenant);
        var debts = new FakeStudentDebtReader(lessons);
        var uow = new FakeUnitOfWork();
        var profiles = new FakeTutorProfileRepository(tenant);
        var dispatches = new FakeNotificationDispatchRepository(tenant);
        var sender = new FakeNotificationSender();
        var options = Options.Create(new NotificationsOptions());

        var now = new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var student = Student.Create("Bob", 100m, CreatedAt).Value.OwnedBy(Tutor);
        students.Items.Add(student);
        var profile = TutorProfile.Create(Tutor, TimeZoneInfo.Utc, CreatedAt).Value;
        profile.UpdateRemindMinutes(30);
        profiles.Items.Add(profile);
        lessons.Items.Add(Lesson.Create(student.Id, now.AddMinutes(15), 60, 100m, CreatedAt).Value.OwnedBy(Tutor));

        var renderer = new NotificationRenderer(options);
        var views = new NotificationViewBuilder(lessons, students, series, debts, clock);
        var reconciler = new NotificationReconciler(
            dispatches, views, renderer, sender, uow, clock, options, NullLogger<NotificationReconciler>.Instance);
        var runner = new NotificationRunner(
            profiles, lessons, dispatches, sender, new NotificationPlanner(), reconciler, views, renderer,
            options, uow, tenant, clock, NullLogger<NotificationRunner>.Instance);

        var services = new ServiceCollection();
        services.AddScoped(_ => runner);
        var provider = services.BuildServiceProvider();

        var poller = new NotificationPollerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
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
