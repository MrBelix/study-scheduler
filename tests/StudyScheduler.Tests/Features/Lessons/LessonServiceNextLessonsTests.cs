using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// Covers the façade's "next lesson per student" projection now that every lesson is a row: the
/// earliest non-cancelled one starting at or after "now", named by its own topic or — when the
/// schedule wrote it and nobody has titled it — by the series it came from.
/// </summary>
public class LessonServiceNextLessonsTests
{
    private const long Tutor = 555;
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // London is on BST (UTC+1) in July/August, so a 16:00 local weekly slot expands to 15:00 UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday; "now" is that morning, before the 15:00 UTC slot.
    private static readonly DateOnly Monday = new(2026, 7, 6);
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonService _sut;

    public LessonServiceNextLessonsTests()
    {
        // The façade runs inside a request's scope, so the rows it can see are that tutor's — the
        // student id is the only thing it still narrows by itself.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _sut = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now));
    }

    private LessonSeries AddSeries(
        DateOnly startDate,
        DateOnly? endDate = null,
        string? title = "Algebra",
        Guid? studentId = null)
    {
        var series = LessonSeries.Create(
            studentId ?? StudentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            startDate, CreatedAt, title: title, endDate: endDate).Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        return series;
    }

    /// <summary>The row the generator writes for one slot of the series.</summary>
    private Lesson AddSeriesLesson(LessonSeries series, DateOnly occurrenceDate)
    {
        var occurrence = series.GetOccurrences(occurrenceDate, occurrenceDate)[0];
        var lesson = Lesson.Create(
            series.StudentId, occurrence.StartUtc, 60, 100m, CreatedAt,
            seriesId: series.Id, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    [Fact]
    public async Task GetNextLessonsAsync_NoLessons_ReturnsNothingForTheStudent()
    {
        // Arrange
        // Nothing scheduled at all.

        // Act
        var next = await _sut.GetNextLessonsAsync();

        // Assert
        Assert.Empty(next);
    }

    [Fact]
    public async Task GetNextLessonsAsync_SeriesLessonMonthsAhead_ReturnsItUnderTheSeriesTitle()
    {
        // Arrange
        // A series starting 2026-08-03 (a Monday) has already written its first lessons out.
        var series = AddSeries(startDate: new DateOnly(2026, 8, 3));
        var first = AddSeriesLesson(series, new DateOnly(2026, 8, 3));
        AddSeriesLesson(series, new DateOnly(2026, 8, 10));

        // Act
        var next = await _sut.GetNextLessonsAsync();

        // Assert
        // The earliest row wins, and an untitled generated lesson reads as the schedule that made it.
        var lesson = next[StudentId];
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 15, 0, 0, TimeSpan.Zero), lesson.StartUtc);
        Assert.Equal(60, lesson.DurationMinutes);
        Assert.Equal("Algebra", lesson.Subject);
        Assert.Equal(first.Id, lesson.LessonId);
    }

    [Fact]
    public async Task GetNextLessonsAsync_SeriesLessonWithATopicOfItsOwn_PrefersTheTopic()
    {
        // Arrange
        var series = AddSeries(startDate: Monday);
        AddSeriesLesson(series, Monday).UpdateTopic("Quadratics");

        // Act
        var next = await _sut.GetNextLessonsAsync();

        // Assert
        Assert.Equal("Quadratics", next[StudentId].Subject);
    }

    [Fact]
    public async Task GetNextLessonsAsync_OnlyPastLessons_ReturnsNothingForTheStudent()
    {
        // Arrange
        var series = AddSeries(startDate: new DateOnly(2026, 6, 1), endDate: new DateOnly(2026, 6, 29));
        AddSeriesLesson(series, new DateOnly(2026, 6, 29));

        // Act
        var next = await _sut.GetNextLessonsAsync();

        // Assert
        Assert.Empty(next);
    }

    [Fact]
    public async Task GetNextLessonsAsync_OneOffBeforeTheSeriesLesson_ReturnsTheOneOff()
    {
        // Arrange
        var series = AddSeries(startDate: Monday);
        AddSeriesLesson(series, Monday);
        // A one-off at 12:00 UTC today, three hours before the series' 15:00 UTC lesson.
        var oneOff = Lesson.Create(
            StudentId, new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero), 60, 100m, CreatedAt,
            topic: "Homework review").Value.OwnedBy(Tutor);
        _lessons.Items.Add(oneOff);

        // Act
        var next = await _sut.GetNextLessonsAsync();

        // Assert
        var lesson = next[StudentId];
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero), lesson.StartUtc);
        Assert.Equal("Homework review", lesson.Subject);
        Assert.Equal(oneOff.Id, lesson.LessonId);
    }

    [Fact]
    public async Task GetNextLessonsAsync_NextLessonCancelled_ReturnsTheFollowingOne()
    {
        // Arrange
        var series = AddSeries(startDate: Monday);
        AddSeriesLesson(series, Monday).ChangeStatus(LessonStatus.Cancelled);
        AddSeriesLesson(series, Monday.AddDays(7));

        // Act
        var next = await _sut.GetNextLessonsAsync();

        // Assert
        // A cancelled lesson is never the answer, so the week after is.
        var lesson = next[StudentId];
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 15, 0, 0, TimeSpan.Zero), lesson.StartUtc);
        Assert.Equal("Algebra", lesson.Subject);
    }

    [Fact]
    public async Task GetNextLessonsAsync_FilteredByStudent_ReturnsOnlyThatStudentsLesson()
    {
        // Arrange
        var otherStudentId = Guid.NewGuid();
        AddSeriesLesson(AddSeries(startDate: Monday), Monday);
        AddSeriesLesson(AddSeries(startDate: Monday, title: "Geometry", studentId: otherStudentId), Monday);

        // Act
        var next = await _sut.GetNextLessonsAsync(otherStudentId);

        // Assert
        var entry = Assert.Single(next);
        Assert.Equal(otherStudentId, entry.Key);
        Assert.Equal("Geometry", entry.Value.Subject);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
