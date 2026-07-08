using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Core.Scheduling;

public class SeriesExpansionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private const long TutorId = 42;

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();

    private SeriesExpansion CreateExpansion() => new(_lessons, _series);

    [Fact]
    public async Task Occurrence_at_utc_plus_14_is_found_although_its_local_date_lies_outside_the_naive_utc_date_range()
    {
        // Pacific/Kiritimati is UTC+14: Monday 2026-07-13 08:00 local is still Sunday evening
        // 18:00 UTC on the 12th. A naive DateOnly range derived from the UTC window ([12th, 12th])
        // would never expand the 13th — the ±2 day local-date slack must cover it.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati");
        var series = LessonSeries.Create(
            TutorId, Guid.NewGuid(), new DateOnly(2026, 6, 1), Weekdays.Monday,
            new TimeOnly(8, 0), 60, zone, Now.AddDays(-40)).Value;
        _series.Series.Add(series);

        var free = await CreateExpansion().GetFreeOccurrencesAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 12, 17, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 12, 19, 0, 0, TimeSpan.Zero));

        var occurrence = Assert.Single(Assert.Single(free).Occurrences);
        Assert.Equal(new DateOnly(2026, 7, 13), occurrence.OccurrenceDate);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero), occurrence.StartUtc);
    }

    [Fact]
    public async Task Occurrence_dates_with_a_materialized_row_are_suppressed()
    {
        var studentId = Guid.NewGuid();
        var series = LessonSeries.Create(
            TutorId, studentId, new DateOnly(2026, 7, 1), Weekdays.Monday,
            new TimeOnly(10, 0), 60, TimeZoneInfo.Utc, Now.AddDays(-30)).Value;
        _series.Series.Add(series);

        // Jul 13 already has its physical row (even a cancelled one governs the slot).
        var materialized = Lesson.Create(
            TutorId, studentId, new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero), 60, 100m, Now,
            seriesId: series.Id, occurrenceDate: new DateOnly(2026, 7, 13)).Value;
        _lessons.Lessons.Add(materialized);

        var free = await CreateExpansion().GetFreeOccurrencesAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

        // Only the untouched Monday (Jul 20) survives.
        var occurrence = Assert.Single(Assert.Single(free).Occurrences);
        Assert.Equal(new DateOnly(2026, 7, 20), occurrence.OccurrenceDate);
    }
}
