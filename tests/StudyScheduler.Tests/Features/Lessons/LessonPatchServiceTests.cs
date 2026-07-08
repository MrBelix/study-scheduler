using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

public class LessonPatchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private const long TutorId = 42;

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeUnitOfWork _uow = new();

    private LessonPatchService CreateService() => new(
        _lessons,
        new LessonOverlapChecker(
            _lessons, _series, new SeriesExpansion(_lessons, _series), NullLogger<LessonOverlapChecker>.Instance),
        _uow,
        NullLogger<LessonPatchService>.Instance);

    private static Lesson CreateLesson(DateTimeOffset? startUtc = null) =>
        Lesson.Create(
            TutorId, Guid.NewGuid(),
            startUtc ?? new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero),
            60, 100m, Now).Value;

    private static UpdateLessonRequest Patch(
        DateTimeOffset? startUtc = null,
        int? durationMinutes = null,
        LessonStatus? status = null,
        decimal? price = null,
        bool? isPaid = null,
        string? topic = null,
        string? description = null) =>
        new(startUtc, durationMinutes, status, price, isPaid, topic, description);

    [Fact]
    public async Task Validation_failure_reports_every_invalid_field_and_never_checks_overlaps_or_saves()
    {
        var lesson = CreateLesson();
        var request = Patch(
            startUtc: lesson.StartUtc.AddHours(1),
            durationMinutes: 5,               // < 15 minimum
            price: -1m,                       // negative
            topic: new string('x', 201));     // > 200 chars

        var outcome = await CreateService().ApplyAsync(lesson, request, TutorId, isNew: false);

        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        Assert.Equal(
            ["DurationMinutes", "Price", "Topic"],
            validation.Failure.Errors.Select(e => e.Field).Order());
        Assert.Empty(_lessons.OverlapQueries);
        Assert.Empty(_lessons.Added);
        Assert.Empty(_lessons.Updated);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Losing_the_materialization_race_returns_the_concurrent_materialization_outcome()
    {
        var seriesId = Guid.NewGuid();
        var lesson = Lesson.Create(
            TutorId, Guid.NewGuid(), new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero),
            60, 100m, Now, seriesId: seriesId, occurrenceDate: new DateOnly(2026, 7, 13)).Value;
        // The unique (SeriesId, OccurrenceDate) index rejects the insert — a concurrent request
        // materialized the same slot first.
        _uow.SaveFailures.Enqueue(new DbUpdateException("duplicate", SqlExceptionFactory.Create(2601)));

        var outcome = await CreateService().ApplyAsync(lesson, Patch(topic: "Algebra"), TutorId, isNew: true);

        Assert.IsType<LessonPatchOutcome.ConcurrentMaterialization>(outcome);
        Assert.Same(lesson, Assert.Single(_lessons.Added));
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task Happy_path_patch_stages_the_update_and_commits_once()
    {
        var lesson = CreateLesson();
        _lessons.Lessons.Add(lesson);

        var outcome = await CreateService().ApplyAsync(
            lesson, Patch(topic: "Algebra", isPaid: true), TutorId, isNew: false);

        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Same(lesson, ok.Lesson);
        Assert.Equal("Algebra", lesson.Topic);
        Assert.True(lesson.IsPaid);
        Assert.Same(lesson, Assert.Single(_lessons.Updated));
        Assert.Empty(_lessons.Added);
        Assert.Equal(1, _uow.SaveCount);
        // No time change and no un-cancelling — the overlap checker must not even be consulted.
        Assert.Empty(_lessons.OverlapQueries);
    }

    [Fact]
    public async Task Rescheduling_into_an_occupied_slot_returns_the_conflicts_and_saves_nothing()
    {
        var lesson = CreateLesson();
        var occupant = CreateLesson(new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        _lessons.Lessons.Add(lesson);
        _lessons.Lessons.Add(occupant);

        var outcome = await CreateService().ApplyAsync(
            lesson, Patch(startUtc: new DateTimeOffset(2026, 7, 13, 12, 30, 0, TimeSpan.Zero)),
            TutorId, isNew: false);

        var conflict = Assert.IsType<LessonPatchOutcome.Conflict>(outcome);
        Assert.Equal(occupant.Id, Assert.Single(conflict.Conflicts).LessonId);
        Assert.Empty(_lessons.Updated);
        Assert.Equal(0, _uow.SaveCount);
    }
}
