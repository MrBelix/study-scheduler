using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

public class LessonOverlapCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private const long TutorId = 42;

    /// <summary>Mirror of the checker's private series-vs-series horizon (104 weeks).</summary>
    private const int SeriesConflictHorizonDays = 728;

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();

    private LessonOverlapChecker CreateChecker() => new(
        _lessons,
        _series,
        new SeriesExpansion(_lessons, _series),
        NullLogger<LessonOverlapChecker>.Instance);

    /// <summary>Weekly Mondays at 10:00 UTC from 2026-07-01 — occurrences on Jul 6/13/20/27.</summary>
    private LessonSeries AddMondaySeries()
    {
        var series = LessonSeries.Create(
            TutorId, Guid.NewGuid(), new DateOnly(2026, 7, 1), Weekdays.Monday,
            new TimeOnly(10, 0), 60, TimeZoneInfo.Utc, Now.AddDays(-30)).Value;
        _series.Series.Add(series);
        return series;
    }

    [Fact]
    public async Task Back_to_back_slots_do_not_conflict()
    {
        // Monday Jul 13: physical lesson 08:00-09:00 and series slot 10:00-11:00. A candidate
        // filling the exact 09:00-10:00 gap starts where the lesson ends and ends where the
        // series slot starts — overlap must be strict on both ends.
        var lesson = Lesson.Create(
            TutorId, Guid.NewGuid(), new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero), 60, 100m, Now).Value;
        _lessons.Lessons.Add(lesson);
        AddMondaySeries();

        var conflicts = await CreateChecker().CheckLessonAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero));

        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task Lesson_conflicts_with_an_unmaterialized_series_slot()
    {
        var series = AddMondaySeries();

        var conflicts = await CreateChecker().CheckLessonAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 13, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 13, 11, 30, 0, TimeSpan.Zero));

        var conflict = Assert.Single(conflicts);
        Assert.Null(conflict.LessonId);
        Assert.Equal(series.Id, conflict.SeriesId);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero), conflict.StartUtc);
    }

    [Fact]
    public async Task Excluded_occurrence_does_not_conflict_with_the_slot_being_materialized()
    {
        var series = AddMondaySeries();
        var slotStart = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);

        // Sanity: without the exclusion the slot collides with its own virtual occurrence.
        var without = await CreateChecker().CheckLessonAsync(TutorId, slotStart, slotStart.AddMinutes(60));
        Assert.Single(without);

        var with = await CreateChecker().CheckLessonAsync(
            TutorId, slotStart, slotStart.AddMinutes(60),
            excludeOccurrence: (series.Id, new DateOnly(2026, 7, 13)));

        Assert.Empty(with);
    }

    // Series-vs-series conflicts are only searched SeriesConflictHorizonDays past the start of
    // the ranges' intersection. Two weekly patterns in fixed-offset zones either collide every
    // week or never, so a "first collision far in the future" needs a zone whose offset changes
    // at a chosen date: candidate runs Mondays 11:00-12:00 UTC; the other runs Mondays 12:00
    // local in a custom zone that is UTC+0 until the shift date and UTC+1 after — back-to-back
    // (no conflict) before the shift, exact collision after it.
    //
    // Both series start Monday 2026-01-05, so the expansion window is
    // [2026-01-04, 2026-01-04 + 728 + 1 = 2028-01-03] and the last Monday searched is 2028-01-03.

    [Fact]
    public async Task Series_collision_on_the_last_day_inside_the_conflict_horizon_is_reported()
    {
        var start = new DateOnly(2026, 1, 5); // a Monday
        var lastSearchedMonday = new DateOnly(2028, 1, 3);
        Assert.Equal(start.AddDays(-1).AddDays(SeriesConflictHorizonDays + 1), lastSearchedMonday);

        // Shift mid-week so the horizon-cap Monday is the very first colliding occurrence.
        var other = LessonSeries.Create(
            TutorId, Guid.NewGuid(), start, Weekdays.Monday, new TimeOnly(12, 0), 60,
            ZoneShiftingOneHourLaterFrom(new DateOnly(2027, 12, 28)), Now).Value;
        _series.Series.Add(other);
        var candidate = LessonSeries.Create(
            TutorId, Guid.NewGuid(), start, Weekdays.Monday, new TimeOnly(11, 0), 60,
            TimeZoneInfo.Utc, Now).Value;

        var conflicts = await CreateChecker().CheckSeriesAsync(candidate);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(other.Id, conflict.SeriesId);
        Assert.Equal(
            new DateTimeOffset(2028, 1, 3, 11, 0, 0, TimeSpan.Zero),
            conflict.StartUtc);
    }

    [Fact]
    public async Task Series_collision_first_occurring_beyond_the_conflict_horizon_is_not_reported()
    {
        var start = new DateOnly(2026, 1, 5); // a Monday

        // Shift one day past the horizon-cap Monday (2028-01-03): the first colliding Monday
        // is 2028-01-10, one week beyond the search window.
        var other = LessonSeries.Create(
            TutorId, Guid.NewGuid(), start, Weekdays.Monday, new TimeOnly(12, 0), 60,
            ZoneShiftingOneHourLaterFrom(new DateOnly(2028, 1, 4)), Now).Value;
        _series.Series.Add(other);
        var candidate = LessonSeries.Create(
            TutorId, Guid.NewGuid(), start, Weekdays.Monday, new TimeOnly(11, 0), 60,
            TimeZoneInfo.Utc, Now).Value;

        var conflicts = await CreateChecker().CheckSeriesAsync(candidate);

        Assert.Empty(conflicts);
    }

    /// <summary>
    /// A UTC+0 zone that becomes UTC+1 from <paramref name="shiftDate"/> on, modeled as a
    /// year-round daylight rule effective from that date.
    /// </summary>
    private static TimeZoneInfo ZoneShiftingOneHourLaterFrom(DateOnly shiftDate)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            shiftDate.ToDateTime(TimeOnly.MinValue),
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 1, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 59, 59, 999), 12, 31));

        return TimeZoneInfo.CreateCustomTimeZone(
            "Test/DelayedShift", TimeSpan.Zero, "Test Delayed Shift", "Test Standard", "Test Shifted", [rule]);
    }
}
