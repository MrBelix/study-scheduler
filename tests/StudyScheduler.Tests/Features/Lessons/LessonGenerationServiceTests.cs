using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

public class LessonGenerationServiceTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    [Fact]
    public async Task RunTickAsync_ResolvesScopedGenerator_FillsTheHorizonOfAnUnwrittenSeries()
    {
        // Arrange
        // The generated rows are the observable signal that the tick resolved the generator from its
        // own scope and awaited it: an open-ended Monday series that predates eager generation.
        // The scope starts tenant-less, exactly as the hosted service's does — the pass takes its
        // tenant from the series it walks.
        var tenant = new TutorContext();
        var lessons = new FakeLessonRepository(tenant);
        var series = new FakeLessonSeriesRepository(tenant);
        var students = new FakeStudentRepository(tenant);
        var uow = new FakeUnitOfWork();
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero));

        series.Items.Add(LessonSeries.Create(
            Guid.NewGuid(),
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, Kyiv).Value,
            new DateOnly(2026, 1, 5),
            CreatedAt,
            price: 500m).Value.OwnedBy(Tutor));

        var services = new ServiceCollection();
        services.AddScoped(_ => new LessonGenerator(
            lessons, series, students, uow, tenant, clock,
            NullLogger<LessonGenerator>.Instance));
        var provider = services.BuildServiceProvider();

        var generation = new LessonGenerationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LessonGenerationService>.Instance);

        // Act
        await generation.RunTickAsync(CancellationToken.None);

        // Assert
        // Nothing before today, everything up to the horizon's last Monday.
        Assert.NotEmpty(lessons.Items);
        Assert.Equal(new DateOnly(2026, 7, 6), lessons.Items[0].OccurrenceDate);
        Assert.Equal(new DateOnly(2026, 11, 2), lessons.Items[^1].OccurrenceDate);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
