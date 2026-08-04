using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// The single addressing scheme of GET/PATCH <c>/lessons/{id}</c>: every lesson is a physical row, so
/// a one-off lesson and a series occurrence answer to the very same thing — the id of that row —
/// while another tutor's row and an id nobody wrote read alike as missing.
/// </summary>
public class LessonByIdEndpointsTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 777;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday; London is on BST (UTC+1) in July, so a 16:00 local slot is 15:00 UTC.
    private static readonly DateOnly Monday = new(2026, 7, 6);
    private static readonly DateTimeOffset MondayStartUtc = new(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);

    // These fixtures set no tutor profile, so the horizon is measured in UTC: four months from
    // 2026-07-06 is 2026-11-06, and that day is still inside the window.
    private static readonly DateTimeOffset LastPlannableStartUtc = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonService _service;

    public LessonByIdEndpointsTests()
    {
        // The id is the whole address a request carries; whose lesson it names is the scope's tenant.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _service = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now));
    }

    [Fact]
    public async Task GetById_OneOffLesson_ReturnsIt()
    {
        // Arrange
        var lesson = AddOneOff(topic: "Homework review");

        // Act
        var result = await GetById(lesson.Id);

        // Assert
        var response = Assert.IsType<Ok<LessonResponse>>(result.Result).Value!;
        Assert.Equal(lesson.Id, response.Id);
        Assert.Equal("Homework review", response.Topic);
        Assert.Null(response.SeriesId);
    }

    [Fact]
    public async Task GetById_GeneratedSeriesLesson_ReturnsTheStoredRow()
    {
        // Arrange
        var series = AddSeries(price: 250m);
        var generated = AddRow(series, Monday);
        generated.UpdateTopic("Quadratics");
        generated.SetPrice(400m);

        // Act
        var result = await GetById(generated.Id);

        // Assert
        // A generated lesson is addressed by its own row id like any other — and answers with its own
        // snapshot, not the series' price.
        var response = Assert.IsType<Ok<LessonResponse>>(result.Result).Value!;
        Assert.Equal(generated.Id, response.Id);
        Assert.Equal(series.Id, response.SeriesId);
        Assert.Equal(Monday, response.OccurrenceDate);
        Assert.Equal("Quadratics", response.Topic);
        Assert.Equal(400m, response.Price);
    }

    [Fact]
    public async Task GetById_AnotherTutorsLesson_ReturnsNotFound()
    {
        // Arrange
        var lesson = Lesson.Create(Guid.NewGuid(), MondayStartUtc, 60, 100m, CreatedAt)
            .Value.OwnedBy(OtherTutor);
        _lessons.Items.Add(lesson);

        // Act
        var result = await GetById(lesson.Id);

        // Assert
        Assert.IsType<NotFound>(result.Result);
    }

    [Fact]
    public async Task GetById_AnotherTutorsSeriesLesson_ReturnsNotFound()
    {
        // Arrange
        var series = LessonSeries.Create(
            Guid.NewGuid(),
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt, price: 100m).Value.OwnedBy(OtherTutor);
        _series.Items.Add(series);
        var theirs = Lesson.Create(
            series.StudentId, MondayStartUtc, 60, 100m, CreatedAt,
            seriesId: series.Id, occurrenceDate: Monday).Value.OwnedBy(OtherTutor);
        _lessons.Items.Add(theirs);

        // Act
        var result = await GetById(theirs.Id);

        // Assert
        // The row exists, but not for this tutor: ownership is part of the lookup, so it reads as missing.
        Assert.IsType<NotFound>(result.Result);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFoundWithoutWriting()
    {
        // Arrange
        // A series whose Monday was never generated — the lesson the tutor can reach is the row, and
        // there is none.
        AddSeries(price: 250m);

        // Act
        var result = await GetById(Guid.NewGuid());

        // Assert
        // Nothing is projected from the rule any more, and a read certainly writes nothing.
        Assert.IsType<NotFound>(result.Result);
        Assert.Empty(_lessons.Items);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task GetMine_GeneratedSeriesLesson_IsListedUnderItsLessonId()
    {
        // Arrange
        var series = AddSeries(price: 250m);
        var generated = AddRow(series, Monday);
        var from = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);

        // Act
        var result = await Endpoints.GetMine(from, from.AddDays(1), null, _lessons, default);

        // Assert
        // The list is the client's entry point: it hands out the row id, and that id addresses the
        // very same lesson by hand.
        var listed = Assert.Single(Assert.IsType<Ok<List<LessonResponse>>>(result.Result).Value!);
        Assert.Equal(generated.Id, listed.Id);

        var byId = Assert.IsType<Ok<LessonResponse>>((await GetById(generated.Id)).Result);
        Assert.Equal(listed.Id, byId.Value!.Id);
    }

    [Fact]
    public async Task Update_GeneratedSeriesLesson_AppliesThePatch()
    {
        // Arrange
        var series = AddSeries(price: 250m);
        var generated = AddRow(series, Monday);

        // Act
        var result = await Update(generated.Id, Patch(topic: "Quadratics"));

        // Assert
        var response = Assert.IsType<Ok<LessonResponse>>(result.Result).Value!;
        Assert.Equal(generated.Id, response.Id);
        Assert.Equal("Quadratics", response.Topic);
        // The patch updated the row that was already there instead of adding a second one.
        Assert.Same(generated, Assert.Single(_lessons.Items));
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNotFoundWithoutWriting()
    {
        // Arrange
        AddSeries();

        // Act
        var result = await Update(Guid.NewGuid(), Patch(topic: "Nope"));

        // Assert
        Assert.IsType<NotFound>(result.Result);
        Assert.Empty(_lessons.Items);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Update_OneOffLesson_AppliesThePatch()
    {
        // Arrange
        var lesson = AddOneOff();

        // Act
        var result = await Update(lesson.Id, Patch(status: LessonStatus.Completed));

        // Assert
        var response = Assert.IsType<Ok<LessonResponse>>(result.Result).Value!;
        Assert.Equal(lesson.Id, response.Id);
        Assert.Equal(LessonStatus.Completed, response.Status);
    }

    [Fact]
    public async Task Update_ReschedulingBeyondThePlanningHorizon_ReturnsValidationProblem()
    {
        // Arrange
        var lesson = AddOneOff();

        // Act — one day past the four months the calendar is planned for.
        var result = await Update(lesson.Id, Patch(startUtc: LastPlannableStartUtc.AddDays(1)));

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("StartUtc", problem.Errors.Keys);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero), lesson.StartUtc);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Update_ReschedulingOntoTheLastDayOfThePlanningHorizon_AppliesThePatch()
    {
        // Arrange
        var lesson = AddOneOff();

        // Act — the edge itself is inside the window.
        var result = await Update(lesson.Id, Patch(startUtc: LastPlannableStartUtc));

        // Assert
        var response = Assert.IsType<Ok<LessonResponse>>(result.Result).Value!;
        Assert.Equal(LastPlannableStartUtc, response.StartUtc);
    }

    [Fact]
    public async Task Update_ReschedulingASeriesLessonBeyondTheHorizon_RefusesBeforeReadingIt()
    {
        // Arrange
        var series = AddSeries();
        var generated = AddRow(series, Monday);

        // Act
        var result = await Update(generated.Id, Patch(startUtc: LastPlannableStartUtc.AddDays(1)));

        // Assert
        // The request is unhonourable whichever lesson it names, so it is answered before the row is
        // even read — and the row stands exactly where it was.
        Assert.IsType<ValidationProblem>(result.Result);
        Assert.Equal(MondayStartUtc, generated.StartUtc);
        Assert.Equal(0, _uow.SaveCount);
    }

    private Task<Results<Ok<LessonResponse>, NotFound>> GetById(Guid id) =>
        Endpoints.GetById(id, _service, default);

    private Task<Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> Update(
        Guid id,
        UpdateLessonRequest request) =>
        Endpoints.Update(id, request, _service, default);

    private static UpdateLessonRequest Patch(
        LessonStatus? status = null, string? topic = null, DateTimeOffset? startUtc = null) =>
        new(startUtc, null, status, null, null, topic, null);

    private LessonSeries AddSeries(decimal? price = 100m)
    {
        var student = Student.Create("Kid", 300m, CreatedAt).Value.OwnedBy(Tutor);
        _students.Items.Add(student);

        var series = LessonSeries.Create(
            student.Id,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt, price: price).Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        return series;
    }

    /// <summary>The row the generator writes for one slot of the series.</summary>
    private Lesson AddRow(LessonSeries series, DateOnly occurrenceDate)
    {
        var occurrence = series.GetOccurrences(occurrenceDate, occurrenceDate)[0];
        var lesson = Lesson.Create(
            series.StudentId, occurrence.StartUtc,
            (int)(occurrence.EndUtc - occurrence.StartUtc).TotalMinutes, series.Price ?? 0m, CreatedAt,
            seriesId: series.Id, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private Lesson AddOneOff(string? topic = null)
    {
        // Noon, well clear of the 15:00 UTC series slot the other fixtures use.
        var lesson = Lesson.Create(
            Guid.NewGuid(), new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero), 60, 100m,
            CreatedAt, topic).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
