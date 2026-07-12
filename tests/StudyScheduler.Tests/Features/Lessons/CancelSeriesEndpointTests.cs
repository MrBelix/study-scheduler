using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// Endpoint-level coverage for <see cref="Endpoints.CancelSeries"/>'s materialize-vs-drop rule for
/// today's occurrence when a series is cancelled effective immediately.
/// </summary>
public class CancelSeriesEndpointTests
{
    private const long Tutor = 555;
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // London is on BST (UTC+1) in July, so a 16:00 local weekly slot expands to 15:00 UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday.
    private static readonly DateOnly Monday = new(2026, 7, 6);

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeStudentRepository _students = new();
    private readonly FakeUnitOfWork _uow = new();

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Tutor.ToString())]));

    private LessonSeries AddSeries()
    {
        // Price set so materialization does not depend on a student rate lookup.
        var series = LessonSeries.Create(
            Tutor, StudentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt, price: 100m).Value;
        _series.Items.Add(series);
        return series;
    }

    private LessonMaterializer Materializer(DateTimeOffset now) =>
        new(_students, new FixedClock(now), NullLogger<LessonMaterializer>.Instance);

    [Fact]
    public async Task CancelSeries_TodayOccurrenceAlreadyStarted_MaterializesAndKeepsIt()
    {
        // Arrange
        var series = AddSeries();
        // Monday 16:00 London = 15:00 UTC; "now" is 16:00 UTC → today's occurrence already started.
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);

        // Act
        await Endpoints.CancelSeries(
            series.Id, Principal(), _series, _lessons, Materializer(now), _uow, new FixedClock(now), default);

        // Assert
        // The already-started still-virtual occurrence is persisted as a physical Scheduled row dated
        // today, so it survives the cancellation instead of being silently dropped.
        var lesson = Assert.Single(_lessons.Items);
        Assert.Equal(Monday, lesson.OccurrenceDate);
        Assert.Equal(series.Id, lesson.SeriesId);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        // EndDate tightened to yesterday, so no future virtual occurrences expand.
        Assert.Equal(Monday.AddDays(-1), series.EndDate);
    }

    [Fact]
    public async Task CancelSeries_TodayOccurrenceStillUpcoming_DropsWithoutMaterializing()
    {
        // Arrange
        var series = AddSeries();
        // "now" is 14:00 UTC, before the 15:00 UTC start → today's occurrence is still upcoming.
        var now = new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.Zero);

        // Act
        await Endpoints.CancelSeries(
            series.Id, Principal(), _series, _lessons, Materializer(now), _uow, new FixedClock(now), default);

        // Assert
        // A still-upcoming today occurrence is left virtual and correctly dropped by CancelAsOf.
        Assert.Empty(_lessons.Items);
        Assert.Equal(Monday.AddDays(-1), series.EndDate);
    }

    [Fact]
    public async Task CancelSeries_TodayOccurrenceAlreadyMaterialized_LeavesExistingRowUntouched()
    {
        // Arrange
        var series = AddSeries();
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);
        // A physical override already exists for today's slot.
        var existing = await Materializer(now).MaterializeSlotAsync(
            series, series.GetOccurrences(Monday, Monday)[0], default);
        _lessons.Items.Add(existing);

        // Act
        await Endpoints.CancelSeries(
            series.Id, Principal(), _series, _lessons, Materializer(now), _uow, new FixedClock(now), default);

        // Assert
        // No duplicate is created — the pre-existing physical row is the one that survives.
        var lesson = Assert.Single(_lessons.Items);
        Assert.Equal(existing.Id, lesson.Id);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
