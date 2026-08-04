using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// The archive cascade behind <see cref="LessonService.StopTeachingAsync"/>: the schedule of a
/// student the tutor stopped teaching is emptied physically — running series ended, future lessons
/// deleted — so that "an archived student owns no future lesson and no running series" is an
/// invariant of the data everything else may lean on.
/// </summary>
public class StopTeachingTests
{
    private const long Tutor = 555;
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly Guid OtherStudentId = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // London is on BST (UTC+1) in July, so a 16:00 local weekly slot expands to 15:00 UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday; "now" is that morning — after the 08:30 lesson has started, before the
    // 15:00 UTC one.
    private static readonly DateOnly Monday = new(2026, 7, 6);
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonService _sut;

    public StopTeachingTests()
    {
        // The cascade runs inside the archiving request's scope: everything it reads and writes is
        // that tutor's, which is why the student id is the only argument it takes.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _sut = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now));
    }

    /// <summary>
    /// A weekly Monday series of the student. <paramref name="endDate"/> is applied through the rule's
    /// own tightening seam rather than the factory, so a series that stopped BEFORE it ever started —
    /// which is exactly what ending one as of today produces — can be set up at all.
    /// </summary>
    private LessonSeries AddSeries(Guid? studentId = null, DateOnly? endDate = null)
    {
        var series = LessonSeries.Create(
            studentId ?? StudentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt, title: "Algebra").Value.OwnedBy(Tutor);
        if (endDate is { } end)
            series.End(end);
        _series.Items.Add(series);
        return series;
    }

    private Lesson AddLesson(
        DateTimeOffset startUtc,
        Guid? studentId = null,
        LessonStatus status = LessonStatus.Scheduled,
        LessonSeries? series = null,
        DateOnly? occurrenceDate = null,
        bool customized = false)
    {
        var lesson = Lesson.Create(
            studentId ?? StudentId, startUtc, 60, 100m, CreatedAt,
            seriesId: series?.Id, occurrenceDate: series is null ? null : occurrenceDate ?? Monday)
            .Value.OwnedBy(Tutor);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        if (customized)
            lesson.MarkCustomized();
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task StopTeachingAsync_RunningSeries_EndsItYesterdayInItsOwnZone()
    {
        // Arrange
        var series = AddSeries();

        // Act
        await _sut.StopTeachingAsync(StudentId);

        // Assert
        // Exactly what cancelling the series by hand does: its last possible lesson day is the day
        // before today in the zone it was scheduled in, so it produces nothing from today on.
        Assert.Equal(new DateOnly(2026, 7, 5), series.EndDate);
        Assert.Same(series, Assert.Single(_series.Items));
    }

    [Fact]
    public async Task StopTeachingAsync_SeriesThatAlreadyEnded_LeavesItsEndDateAlone()
    {
        // Arrange — it stopped yesterday of its own accord.
        var series = AddSeries(endDate: new DateOnly(2026, 7, 5));

        // Act
        await _sut.StopTeachingAsync(StudentId);

        // Assert — nothing to stop, so nothing is rewritten.
        Assert.Equal(new DateOnly(2026, 7, 5), series.EndDate);
    }

    [Fact]
    public async Task StopTeachingAsync_FutureLessons_DeletesThemAll()
    {
        // Arrange
        var series = AddSeries();
        AddLesson(Utc(8, 12));                                                  // one-off ahead
        AddLesson(Utc(6, 15), series: series);                                  // today's generated row
        AddLesson(Utc(13, 15), series: series, occurrenceDate: Monday.AddDays(7), customized: true);
        AddLesson(Utc(20, 15), series: series, occurrenceDate: Monday.AddDays(14),
            status: LessonStatus.Cancelled);

        // Act
        await _sut.StopTeachingAsync(StudentId);

        // Assert
        // The tutor stopped teaching them, so nothing of theirs is still ahead — one-offs, generated
        // rows, the occurrences somebody edited by hand and the ones already cancelled alike.
        Assert.Empty(_lessons.Items);
    }

    [Fact]
    public async Task StopTeachingAsync_CompletedLessonAhead_KeepsIt()
    {
        // Arrange — recorded as done (and paid) before its scheduled end, as the bot's buttons allow.
        var completed = AddLesson(Utc(20, 15), status: LessonStatus.Completed);
        AddLesson(Utc(21, 15));

        // Act
        await _sut.StopTeachingAsync(StudentId);

        // Assert
        // A completed lesson happened and carries the payment the debt dashboard counts: it is
        // history, not plan, wherever its date sits.
        Assert.Same(completed, Assert.Single(_lessons.Items));
    }

    [Fact]
    public async Task StopTeachingAsync_PastAndRunningLessons_KeepsThem()
    {
        // Arrange
        var past = AddLesson(Utc(1, 15), status: LessonStatus.Completed);
        var yesterday = AddLesson(Utc(5, 15), status: LessonStatus.Cancelled);
        // Started at 08:30, still running at 09:00 — the cut is by instant, not by day.
        var running = AddLesson(Utc(6, 8, 30));
        AddLesson(Utc(6, 15));

        // Act
        await _sut.StopTeachingAsync(StudentId);

        // Assert
        Assert.Equal([past, yesterday, running], _lessons.Items);
    }

    [Fact]
    public async Task StopTeachingAsync_AnotherStudentOfTheSameTutor_LeavesTheirScheduleAlone()
    {
        // Arrange
        var theirSeries = AddSeries(studentId: OtherStudentId);
        var theirLesson = AddLesson(Utc(8, 12), studentId: OtherStudentId);
        AddSeries();
        AddLesson(Utc(8, 14));

        // Act
        await _sut.StopTeachingAsync(StudentId);

        // Assert
        // Only the archived student's schedule is swept: everyone else keeps theirs untouched.
        Assert.Null(theirSeries.EndDate);
        Assert.Same(theirLesson, Assert.Single(_lessons.Items));
    }

    [Fact]
    public async Task StopTeachingAsync_SeriesAndLessons_CommitsOnce()
    {
        // Arrange
        var series = AddSeries();
        AddLesson(Utc(6, 15), series: series);
        AddLesson(Utc(8, 12));

        // Act
        await _sut.StopTeachingAsync(StudentId);

        // Assert
        // One transaction for the whole cascade: the schedule either forgets the student or does not.
        Assert.Equal(1, _uow.SaveCount);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
