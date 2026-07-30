using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using Xunit;
// The Student constant below shadows the domain type's simple name.
using DomainStudent = StudyScheduler.Domain.Students.Student;

namespace StudyScheduler.Tests.Features.Lessons;

public class LessonOverlapCheckerTests
{
    private const long Tutor = 555;
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeStudentRepository _students = new();
    private readonly LessonOverlapChecker _sut;

    public LessonOverlapCheckerTests() =>
        _sut = new LessonOverlapChecker(
            _lessons, _series, new SeriesExpansion(_lessons, _series), _students,
            NullLogger<LessonOverlapChecker>.Instance);

    private static DateTimeOffset Utc(int day, int hour) => new(2026, 7, day, hour, 0, 0, TimeSpan.Zero);

    private Lesson AddLesson(
        int day, int startHour, int duration = 60, LessonStatus status = LessonStatus.Scheduled, Guid? studentId = null)
    {
        var lesson = Lesson.Create(Tutor, studentId ?? Student, Utc(day, startHour), duration, 0m, CreatedAt).Value;
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    // Mon/Thu 16:00 London == 15:00 UTC in July.
    private LessonSeries MondayThursdaySeries(bool addToRepo = true, Guid? studentId = null)
    {
        var series = LessonSeries.Create(
            Tutor, studentId ?? Student,
            WeeklyPattern.Create(Weekdays.Monday | Weekdays.Thursday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt).Value;
        if (addToRepo)
            _series.Items.Add(series);
        return series;
    }

    /// <summary>Registers a student of the tutor with the given status and returns its id.</summary>
    private Guid AddStudent(StudentStatus status)
    {
        var student = DomainStudent.Create(Tutor, "S", 100m, CreatedAt).Value;
        student.ChangeStatus(status);
        _students.Items.Add(student);
        return student.Id;
    }

    [Fact]
    public async Task CheckLesson_OverlappingPhysical_ReturnsConflict()
    {
        var lesson = AddLesson(day: 20, startHour: 15);

        var conflicts = await _sut.CheckLessonAsync(Tutor, Utc(20, 15) + TimeSpan.FromMinutes(30), Utc(20, 16) + TimeSpan.FromMinutes(30));

        Assert.Equal(lesson.Id, Assert.Single(conflicts).LessonId);
    }

    [Fact]
    public async Task CheckLesson_BackToBack_NoConflict()
    {
        AddLesson(day: 20, startHour: 15); // 15:00–16:00

        Assert.Empty(await _sut.CheckLessonAsync(Tutor, Utc(20, 16), Utc(20, 17)));
    }

    [Fact]
    public async Task CheckLesson_CancelledPhysical_Ignored()
    {
        AddLesson(day: 20, startHour: 15, status: LessonStatus.Cancelled);

        Assert.Empty(await _sut.CheckLessonAsync(Tutor, Utc(20, 15), Utc(20, 16)));
    }

    [Fact]
    public async Task CheckLesson_VirtualSeriesOccurrence_ReturnsConflict()
    {
        var series = MondayThursdaySeries();

        // Monday 2026-07-06 slot is 15:00–16:00 UTC.
        var conflicts = await _sut.CheckLessonAsync(Tutor, Utc(6, 15), Utc(6, 16));

        Assert.Equal(series.Id, Assert.Single(conflicts).SeriesId);
    }

    [Fact]
    public async Task CheckLesson_ExcludeOccurrence_SkipsThatSlot()
    {
        var series = MondayThursdaySeries();

        var conflicts = await _sut.CheckLessonAsync(
            Tutor, Utc(6, 15), Utc(6, 16),
            excludeOccurrence: new SeriesSlot(series.Id, new DateOnly(2026, 7, 6)));

        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task CheckLesson_OverlapsActiveStudentLesson_ReturnsConflict()
    {
        // Arrange
        var active = AddStudent(StudentStatus.Active);
        var lesson = AddLesson(day: 20, startHour: 15, studentId: active);

        // Act
        var conflicts = await _sut.CheckLessonAsync(Tutor, Utc(20, 15), Utc(20, 16));

        // Assert
        Assert.Equal(lesson.Id, Assert.Single(conflicts).LessonId);
    }

    [Fact]
    public async Task CheckLesson_OverlapsArchivedStudentLesson_NoConflict()
    {
        // Arrange
        var archived = AddStudent(StudentStatus.Archived);
        AddLesson(day: 20, startHour: 15, studentId: archived);

        // Act
        var conflicts = await _sut.CheckLessonAsync(Tutor, Utc(20, 15), Utc(20, 16));

        // Assert
        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task CheckLesson_OverlapsArchivedStudentSeriesOccurrence_NoConflict()
    {
        // Arrange
        var archived = AddStudent(StudentStatus.Archived);
        MondayThursdaySeries(studentId: archived);

        // Act — Monday 2026-07-06 slot is 15:00–16:00 UTC.
        var conflicts = await _sut.CheckLessonAsync(Tutor, Utc(6, 15), Utc(6, 16));

        // Assert
        Assert.Empty(conflicts);
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
            Tutor, Student, WeeklyPattern.Create(Weekdays.Tuesday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt).Value;
        _series.Items.Add(tuesday);
        var candidate = MondayThursdaySeries(addToRepo: false);

        Assert.Empty(await _sut.CheckSeriesAsync(candidate));
    }

    [Fact]
    public async Task CheckSeries_CollidesWithArchivedStudentLesson_NoConflict()
    {
        // Arrange
        var archived = AddStudent(StudentStatus.Archived);
        AddLesson(day: 6, startHour: 15, studentId: archived); // Monday 15:00–16:00 UTC
        var candidate = MondayThursdaySeries(addToRepo: false);

        // Act
        var conflicts = await _sut.CheckSeriesAsync(candidate);

        // Assert
        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task CheckSeries_CollidesWithArchivedStudentSeries_NoConflict()
    {
        // Arrange
        var archived = AddStudent(StudentStatus.Archived);
        MondayThursdaySeries(studentId: archived);             // in repo, same pattern
        var candidate = MondayThursdaySeries(addToRepo: false);

        // Act
        var conflicts = await _sut.CheckSeriesAsync(candidate);

        // Assert
        Assert.Empty(conflicts);
    }
}
