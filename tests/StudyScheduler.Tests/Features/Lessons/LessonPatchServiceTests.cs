using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

public class LessonPatchServiceTests
{
    private const long Tutor = 555;
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonPatchService _sut;

    public LessonPatchServiceTests()
    {
        var overlap = new LessonOverlapChecker(
            _lessons, _series, new SeriesExpansion(_lessons, _series), NullLogger<LessonOverlapChecker>.Instance);
        _sut = new LessonPatchService(_lessons, overlap, _uow, NullLogger<LessonPatchService>.Instance);
    }

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) => new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    private Lesson AddLesson(int day, int hour, int duration = 60, LessonStatus status = LessonStatus.Scheduled)
    {
        var lesson = Lesson.Create(Tutor, Student, Utc(day, hour), duration, 100m, CreatedAt).Value;
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private static UpdateLessonRequest Patch(
        DateTimeOffset? startUtc = null, int? duration = null, LessonStatus? status = null,
        decimal? price = null, bool? isPaid = null, string? topic = null, string? description = null) =>
        new(startUtc, duration, status, price, isPaid, topic, description);

    [Fact]
    public async Task Cancel_SkipsOverlapCheck_AndSaves()
    {
        var lesson = AddLesson(day: 20, hour: 15);
        AddLesson(day: 20, hour: 15); // an overlapping lesson — irrelevant when cancelling

        var outcome = await _sut.ApplyAsync(lesson, Patch(status: LessonStatus.Cancelled), Tutor, isNew: false);

        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(LessonStatus.Cancelled, ok.Lesson.Status);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task Reschedule_ToFreeTime_UpdatesTimes()
    {
        var lesson = AddLesson(day: 20, hour: 15);

        var outcome = await _sut.ApplyAsync(lesson, Patch(startUtc: Utc(21, 15)), Tutor, isNew: false);

        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(Utc(21, 15), ok.Lesson.StartUtc);
        Assert.Equal(Utc(21, 16), ok.Lesson.EndUtc);
    }

    [Fact]
    public async Task Reschedule_ToConflict_ReturnsConflict()
    {
        var lesson = AddLesson(day: 20, hour: 15);
        AddLesson(day: 21, hour: 15); // occupies the target slot

        var outcome = await _sut.ApplyAsync(lesson, Patch(startUtc: Utc(21, 15, 30)), Tutor, isNew: false);

        Assert.IsType<LessonPatchOutcome.Conflict>(outcome);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task ChangeDuration_OutOfRange_ReturnsValidation()
    {
        var lesson = AddLesson(day: 20, hour: 15);

        var outcome = await _sut.ApplyAsync(lesson, Patch(duration: 601), Tutor, isNew: false);

        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        Assert.Equal("Lesson.DurationOutOfRange", Assert.Single(validation.Failure.Errors).Code);
    }

    [Fact]
    public async Task UnCancel_RunsOverlapCheck_AndConflicts()
    {
        var cancelled = AddLesson(day: 20, hour: 15, status: LessonStatus.Cancelled);
        AddLesson(day: 20, hour: 15); // scheduled lesson now occupying the slot

        var outcome = await _sut.ApplyAsync(cancelled, Patch(status: LessonStatus.Scheduled), Tutor, isNew: false);

        Assert.IsType<LessonPatchOutcome.Conflict>(outcome);
    }

    [Fact]
    public async Task Materialize_ExcludeOccurrence_DoesNotConflictWithOwnSlot()
    {
        var series = LessonSeries.Create(
            Tutor, Student, WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt).Value;
        _series.Items.Add(series);
        var occurrence = series.GetOccurrences(new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 6))[0];
        var materialized = Lesson.Create(
            Tutor, Student, occurrence.StartUtc, 60, 0m, CreatedAt,
            seriesId: series.Id, occurrenceDate: new DateOnly(2026, 7, 6)).Value;

        // Reschedule the just-materialized slot within its own virtual occurrence's span.
        var request = Patch(startUtc: occurrence.StartUtc.AddMinutes(30));

        var withExclude = await _sut.ApplyAsync(
            materialized, request, Tutor, isNew: true,
            excludeOccurrence: new SeriesSlot(series.Id, new DateOnly(2026, 7, 6)));

        Assert.IsType<LessonPatchOutcome.Ok>(withExclude);
    }

    [Fact]
    public async Task Materialize_WithoutExclude_ConflictsWithOwnVirtualSlot()
    {
        var series = LessonSeries.Create(
            Tutor, Student, WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt).Value;
        _series.Items.Add(series);
        var occurrence = series.GetOccurrences(new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 6))[0];
        var materialized = Lesson.Create(
            Tutor, Student, occurrence.StartUtc, 60, 0m, CreatedAt,
            seriesId: series.Id, occurrenceDate: new DateOnly(2026, 7, 6)).Value;

        var outcome = await _sut.ApplyAsync(
            materialized, Patch(startUtc: occurrence.StartUtc.AddMinutes(30)), Tutor, isNew: true);

        Assert.IsType<LessonPatchOutcome.Conflict>(outcome);
    }
}
