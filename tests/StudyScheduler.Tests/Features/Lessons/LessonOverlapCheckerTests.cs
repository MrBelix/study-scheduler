using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// The conflict detector as it now stands: it compares the tenant's rows and rules, and knows
/// nothing about students. Archiving one ends their series and deletes their future lessons
/// (<see cref="LessonService.StopTeachingAsync"/>), so nothing of theirs is left ahead to filter out.
/// </summary>
public class LessonOverlapCheckerTests
{
    private const long Tutor = 555;
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly LessonOverlapChecker _sut;

    public LessonOverlapCheckerTests()
    {
        // The checker compares one tutor's calendar because that is all its scope can read — whose
        // calendar it is is never an argument.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _sut = new LessonOverlapChecker(
            _lessons, _series, _tenant, NullLogger<LessonOverlapChecker>.Instance);
    }

    private static DateTimeOffset Utc(int day, int hour) => new(2026, 7, day, hour, 0, 0, TimeSpan.Zero);

    private Lesson AddLesson(
        int day, int startHour, int duration = 60, LessonStatus status = LessonStatus.Scheduled, Guid? studentId = null)
    {
        var lesson = Lesson.Create(studentId ?? Student, Utc(day, startHour), duration, 0m, CreatedAt).Value.OwnedBy(Tutor);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    // Mon/Thu 16:00 London == 15:00 UTC in July.
    private LessonSeries MondayThursdaySeries(bool addToRepo = true, Guid? studentId = null)
    {
        var series = LessonSeries.Create(
            studentId ?? Student,
            WeeklyPattern.Create(Weekdays.Monday | Weekdays.Thursday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt).Value.OwnedBy(Tutor);
        if (addToRepo)
            _series.Items.Add(series);
        return series;
    }

    /// <summary>The row the generator writes for one slot of the series.</summary>
    private Lesson AddSeriesLesson(LessonSeries series, DateOnly occurrenceDate)
    {
        var occurrence = series.GetOccurrences(occurrenceDate, occurrenceDate)[0];
        var lesson = Lesson.Create(
            series.StudentId, occurrence.StartUtc, 60, 0m, CreatedAt,
            seriesId: series.Id, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    [Fact]
    public async Task CheckLesson_OverlappingPhysical_ReturnsConflict()
    {
        var lesson = AddLesson(day: 20, startHour: 15);

        var conflicts = await _sut.CheckLessonAsync(
            Utc(20, 15) + TimeSpan.FromMinutes(30), Utc(20, 16) + TimeSpan.FromMinutes(30));

        Assert.Equal(lesson.Id, Assert.Single(conflicts).LessonId);
    }

    [Fact]
    public async Task CheckLesson_BackToBack_NoConflict()
    {
        AddLesson(day: 20, startHour: 15); // 15:00–16:00

        Assert.Empty(await _sut.CheckLessonAsync(Utc(20, 16), Utc(20, 17)));
    }

    [Fact]
    public async Task CheckLesson_CancelledPhysical_Ignored()
    {
        AddLesson(day: 20, startHour: 15, status: LessonStatus.Cancelled);

        Assert.Empty(await _sut.CheckLessonAsync(Utc(20, 15), Utc(20, 16)));
    }

    [Fact]
    public async Task CheckLesson_OverlapsGeneratedSeriesLesson_ReportsItUnderItsLessonId()
    {
        // Arrange
        // A series occupies a moment through the rows it generated — there is nothing else to check.
        var series = MondayThursdaySeries();
        var generated = AddSeriesLesson(series, new DateOnly(2026, 7, 6));

        // Act — Monday 2026-07-06's lesson is 15:00–16:00 UTC.
        var conflicts = await _sut.CheckLessonAsync(Utc(6, 15), Utc(6, 16));

        // Assert
        // The conflict names the row itself, and says which series it came from.
        var conflict = Assert.Single(conflicts);
        Assert.Equal(generated.Id, conflict.LessonId);
        Assert.Equal(series.Id, conflict.SeriesId);
    }

    [Fact]
    public async Task CheckLesson_OverlapsLessonsOfDifferentStudents_ReportsEveryOne()
    {
        // Arrange
        // Two students of the same tutor, both booked on the moment being asked about.
        var one = AddLesson(day: 20, startHour: 15);
        var another = AddLesson(day: 20, startHour: 15, studentId: Guid.NewGuid());

        // Act
        var conflicts = await _sut.CheckLessonAsync(Utc(20, 15), Utc(20, 16));

        // Assert
        // Whose lesson a row is never decides anything here: the checker compares rows, and every
        // stored row is a row the tutor is expected to teach — an archived student has none ahead.
        Assert.Equal(
            new[] { one.Id, another.Id }.Order(),
            conflicts.Select(c => c.LessonId!.Value).Order());
    }

    [Fact]
    public async Task CheckSeries_CollidesWithExistingLesson_ReturnsConflict()
    {
        var lesson = AddLesson(day: 6, startHour: 15); // Monday 15:00–16:00 UTC
        var candidate = MondayThursdaySeries(addToRepo: false);

        var conflicts = await _sut.CheckSeriesAsync(candidate);

        Assert.Contains(conflicts, c => c.LessonId == lesson.Id);
    }

    [Fact]
    public async Task CheckSeries_CollidesWithOtherSeries_ReturnsConflict()
    {
        var other = MondayThursdaySeries();                    // in repo
        var candidate = MondayThursdaySeries(addToRepo: false); // same pattern

        var conflicts = await _sut.CheckSeriesAsync(candidate);

        Assert.Contains(conflicts, c => c.SeriesId == other.Id);
    }

    [Fact]
    public async Task CheckSeries_NoOverlap_Empty()
    {
        // Other series on Tuesday; candidate Mon/Thu — disjoint weekdays.
        var tuesday = LessonSeries.Create(
            Student, WeeklyPattern.Create(Weekdays.Tuesday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt).Value.OwnedBy(Tutor);
        _series.Items.Add(tuesday);
        var candidate = MondayThursdaySeries(addToRepo: false);

        Assert.Empty(await _sut.CheckSeriesAsync(candidate));
    }

    [Fact]
    public async Task CheckSeries_CollidesWithTheSeriesOfAnotherStudent_ReturnsConflict()
    {
        // Arrange
        // The tutor cannot be in two places at once, so another student's schedule blocks the slot
        // exactly as their own would — the rule holds for every student the tutor still teaches.
        var other = MondayThursdaySeries(studentId: Guid.NewGuid());
        var candidate = MondayThursdaySeries(addToRepo: false);

        // Act
        var conflicts = await _sut.CheckSeriesAsync(candidate);

        // Assert
        Assert.Contains(conflicts, c => c.SeriesId == other.Id);
    }
}
