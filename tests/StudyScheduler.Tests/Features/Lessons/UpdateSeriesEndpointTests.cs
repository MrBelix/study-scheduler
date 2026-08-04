using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// Endpoint-level coverage for <see cref="Endpoints.UpdateSeries"/> as a FULL edit: the weekly
/// schedule is mutable now, so an update rewrites the lessons the series already generated —
/// sweeping what the new rule no longer accounts for and letting the generator refill the horizon.
/// What <c>keepCustomized</c> spares, what a price change does on its own, and what the sweep must
/// never touch (anything that already started, and completed lessons wherever they sit).
/// </summary>
public class UpdateSeriesEndpointTests
{
    private const long Tutor = 555;
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // London is on BST (UTC+1) in July, so a 16:00 local weekly slot expands to 15:00 UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday, and "now" is that morning — every 15:00 UTC slot from it on is future.
    private static readonly DateOnly FirstMonday = new(2026, 7, 6);
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);

    // The four-month planning horizon anchored on today holds 18 Mondays (and 18 Tuesdays).
    private const int WeekdaysInHorizon = 18;

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonService _service;

    public UpdateSeriesEndpointTests()
    {
        // The edit reaches the series the scope owns, and the rows it rewrites are read back through
        // the same tenant — no id is threaded through the call.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _service = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now));
    }

    [Fact]
    public async Task UpdateSeries_EndDateSetOnOpenEndedSeries_RemovesOnlyTheLessonsBeyondIt()
    {
        // Arrange
        var series = AddSeries();
        var kept = AddRow(series, new DateOnly(2026, 7, 13));    // inside the new window
        var dropped = AddRow(series, new DateOnly(2026, 7, 27)); // beyond it

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, new DateOnly(2026, 7, 20)));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(new DateOnly(2026, 7, 20), response.Series.EndDate);
        // A tightened window invalidates what falls outside it and nothing else — the lesson still
        // covered keeps its row rather than being churned for a change that never reached it.
        Assert.Contains(kept, _lessons.Items);
        Assert.DoesNotContain(dropped, _lessons.Items);
        Assert.Equal(dropped.Id, Assert.Single(response.RemovedLessons).Id);
    }

    [Fact]
    public async Task UpdateSeries_EndDateExtended_ChecksOnlyTheNewlyExposedWindowAndFillsIt()
    {
        // Arrange
        var series = AddSeries(end: new DateOnly(2026, 7, 20));
        // The series' own last slot, already generated. It sits in the window that was booked long
        // ago, so extending past it must not report the series against a lesson it owns itself.
        var last = AddRow(series, new DateOnly(2026, 7, 20));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, new DateOnly(2026, 8, 31)));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(new DateOnly(2026, 8, 31), response.Series.EndDate);
        Assert.Empty(response.RemovedLessons);
        Assert.Contains(last, _lessons.Items);
        // Every Monday from today to the new end now has a row, and not one beyond it.
        Assert.Equal(9, _lessons.Items.Count);
        Assert.Equal(new DateOnly(2026, 8, 31), _lessons.Items.Max(l => l.OccurrenceDate));
    }

    [Fact]
    public async Task UpdateSeries_ExtensionCollidesWithAnotherSeries_ReturnsConflictAndSavesNothing()
    {
        // Arrange
        var series = AddSeries(end: new DateOnly(2026, 7, 20));
        // Same weekday and time, but it only starts after the current window ends: today the two never
        // meet — extending is exactly what makes them collide.
        var other = AddSeries(start: new DateOnly(2026, 7, 27));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, new DateOnly(2026, 8, 31)));

        // Assert
        var conflict = Assert.IsType<Conflict<LessonConflictResponse>>(result.Result).Value!;
        Assert.Equal(other.Id, Assert.Single(conflict.Conflicts).SeriesId);
        Assert.Equal(new DateOnly(2026, 7, 20), series.EndDate); // refused, so nothing moved
        Assert.Equal(0, _uow.SaveCount);
        Assert.Empty(_lessons.Items);
    }

    [Fact]
    public async Task UpdateSeries_EndDateExtendedBackOverAKeptCompletedLesson_IsNotBlockedByIt()
    {
        // Arrange
        // A completed lesson beyond the end date the series was just tightened to: the sweep spares
        // it as history, so the series still owns that date.
        var series = AddSeries();
        var completed = AddRow(series, new DateOnly(2026, 8, 3), status: LessonStatus.Completed);
        await Update(series.Id, new UpdateLessonSeriesRequest(null, null, new DateOnly(2026, 7, 20)));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, new DateOnly(2026, 8, 31)));

        // Assert
        // Re-covering its own dates is not a collision — otherwise a tightening could never be undone.
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(new DateOnly(2026, 8, 31), response.Series.EndDate);
        Assert.Contains(completed, _lessons.Items);
    }

    [Fact]
    public async Task UpdateSeries_ClearEndDate_MakesTheSeriesOpenEndedAndFillsTheHorizon()
    {
        // Arrange
        var series = AddSeries(end: new DateOnly(2026, 7, 20));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, null, ClearEndDate: true));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Null(response.Series.EndDate);
        Assert.Null(series.EndDate);
        // The window the change exposes is filled right away, exactly as creating a series fills it.
        Assert.Equal(WeekdaysInHorizon, _lessons.Items.Count);
    }

    [Fact]
    public async Task UpdateSeries_EndDateAndClearEndDateTogether_ReturnsValidationProblem()
    {
        // Arrange
        var series = AddSeries(end: new DateOnly(2026, 7, 20));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(
            null, null, new DateOnly(2026, 8, 31), ClearEndDate: true));

        // Assert
        // A null field means "not provided", so the two fields contradict each other — the request is
        // refused instead of one of them silently winning.
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("EndDate", problem.Errors.Keys);
        Assert.Equal(new DateOnly(2026, 7, 20), series.EndDate);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_EndDateBeforeStartDate_ReturnsValidationProblem()
    {
        // Arrange
        var series = AddSeries();

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, new DateOnly(2026, 7, 1)));

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("EndDate", problem.Errors.Keys);
        Assert.Null(series.EndDate);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_InvalidWeekdays_ReturnsValidationProblemWithoutTouchingTheSchedule()
    {
        // Arrange
        var series = AddSeries();

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, null, Weekdays: Weekdays.None));

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("Weekdays", problem.Errors.Keys);
        Assert.Equal(Weekdays.Monday, series.Pattern.Days);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_TitleOnly_TouchesNoLesson()
    {
        // Arrange
        // A cancelled series: its end date is the day BEFORE the already-started occurrence whose row
        // the cancellation deliberately kept. Renaming it must not sweep that row away.
        var series = AddSeries();
        series.End(new DateOnly(2026, 7, 12));
        var preserved = AddRow(series, new DateOnly(2026, 7, 13));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest("Physics", null, null));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal("Physics", response.Series.Title);
        Assert.Equal(new DateOnly(2026, 7, 12), response.Series.EndDate);
        Assert.Contains(preserved, _lessons.Items);
        Assert.Empty(response.RemovedLessons);
        // The schedule did not move, so there is nothing to sweep and nothing to generate.
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_ScheduleResentUnchanged_TouchesNoLesson()
    {
        // Arrange
        // The edit form sends the whole schedule back whether or not the tutor moved anything, so an
        // unchanged pattern must be recognised as unchanged rather than regenerated over.
        var series = AddSeries();
        var existing = AddRow(series, FirstMonday);

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(
            null, null, null,
            Weekdays: Weekdays.Monday, StartTimeLocal: new TimeOnly(16, 0), DurationMinutes: 60));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Empty(response.RemovedLessons);
        Assert.Same(existing, Assert.Single(_lessons.Items));
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_PriceOnly_RepricesTheUntouchedFutureLessonsOnly()
    {
        // Arrange
        var series = AddSeries(start: new DateOnly(2026, 6, 1));
        var past = AddRow(series, new DateOnly(2026, 6, 29));
        var generated = AddRow(series, new DateOnly(2026, 7, 13));
        var edited = AddRow(series, new DateOnly(2026, 7, 20), price: 250m, customized: true);

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, 400m, null));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(400m, response.Series.Price);
        // Rows are written months ahead, so the rule's new price has to reach the ones nobody touched.
        Assert.Equal(400m, generated.Price);
        // A hand-priced occurrence keeps what it was given, and the past is never rewritten.
        Assert.Equal(250m, edited.Price);
        Assert.Equal(100m, past.Price);
        // Nothing moved: no lesson is deleted, none is generated, one commit does it.
        Assert.Empty(response.RemovedLessons);
        Assert.Equal(3, _lessons.Items.Count);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_EndDateAndPriceChangedTogether_RepricesTheKeptLessonsToo()
    {
        // Arrange
        var series = AddSeries(end: new DateOnly(2026, 7, 20));
        var kept = AddRow(series, new DateOnly(2026, 7, 13));
        var edited = AddRow(series, new DateOnly(2026, 7, 20), price: 250m, customized: true);

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, 400m, new DateOnly(2026, 8, 31)));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(400m, response.Series.Price);
        // The window moved, so the rows it still covers stay standing — and the new price has to reach
        // them as well, or the tutor would be left with an old-priced middle and a new-priced tail.
        Assert.Contains(kept, _lessons.Items);
        Assert.Equal(400m, kept.Price);
        Assert.All(_lessons.Items.Where(l => !l.IsCustomized), l => Assert.Equal(400m, l.Price));
        // A hand-priced occurrence still keeps what it was given.
        Assert.Equal(250m, edited.Price);
        // Nothing was lost: extending only added the Mondays beyond the old end date.
        Assert.Empty(response.RemovedLessons);
    }

    [Fact]
    public async Task UpdateSeries_PatternChangedKeepingCustomized_SparesThemAndRegeneratesTheRest()
    {
        // Arrange
        var series = AddSeries();
        var firstMonday = AddRow(series, FirstMonday);
        var edited = AddRow(series, new DateOnly(2026, 7, 13), customized: true);
        var lastMonday = AddRow(series, new DateOnly(2026, 7, 20));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, null, Weekdays: Weekdays.Tuesday));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(Weekdays.Tuesday, response.Series.Weekdays);
        // The hand-edited occurrence carries a decision of its own, so it stays where the tutor put it.
        Assert.Contains(edited, _lessons.Items);
        Assert.DoesNotContain(firstMonday, _lessons.Items);
        Assert.DoesNotContain(lastMonday, _lessons.Items);
        // The new rule fills the horizon; the Mondays it no longer places are reported as lost.
        Assert.Equal(WeekdaysInHorizon, _lessons.Items.Count(l => l.OccurrenceDate!.Value.DayOfWeek == DayOfWeek.Tuesday));
        Assert.Equal(WeekdaysInHorizon + 1, _lessons.Items.Count);
        Assert.Equal(
            new HashSet<Guid> { firstMonday.Id, lastMonday.Id },
            response.RemovedLessons.Select(l => l.Id).ToHashSet());
    }

    [Fact]
    public async Task UpdateSeries_PatternChangedNotKeepingCustomized_ReplacesEveryFutureLesson()
    {
        // Arrange
        var series = AddSeries();
        AddRow(series, FirstMonday);
        var edited = AddRow(series, new DateOnly(2026, 7, 13), price: 250m, customized: true);

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(
            null, null, null, StartTimeLocal: new TimeOnly(18, 0), KeepCustomized: false));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(new TimeOnly(18, 0), response.Series.StartTimeLocal);
        Assert.DoesNotContain(edited, _lessons.Items);
        Assert.All(_lessons.Items, l => Assert.False(l.IsCustomized));
        // 18:00 London in July is 17:00 UTC — every future lesson sits at the new wall clock.
        Assert.Equal(WeekdaysInHorizon, _lessons.Items.Count);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 17, 0, 0, TimeSpan.Zero), _lessons.Items[0].StartUtc);
        // Both swept dates got a lesson again, so nothing was actually lost: the client must not
        // announce cancellations for occurrences that are still on the calendar.
        Assert.Empty(response.RemovedLessons);
    }

    [Fact]
    public async Task UpdateSeries_PatternChanged_LeavesStartedAndCompletedLessonsAlone()
    {
        // Arrange
        var series = AddSeries(start: new DateOnly(2026, 6, 1));
        var past = AddRow(series, new DateOnly(2026, 6, 29));
        var scheduled = AddRow(series, new DateOnly(2026, 7, 13));
        var completed = AddRow(series, new DateOnly(2026, 7, 20), status: LessonStatus.Completed);

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(
            null, null, null, Weekdays: Weekdays.Tuesday, KeepCustomized: false));

        // Assert
        // Even the most destructive mode only reaches lessons that have not started, and never a
        // completed one: it happened and carries its money.
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Contains(past, _lessons.Items);
        Assert.Contains(completed, _lessons.Items);
        Assert.DoesNotContain(scheduled, _lessons.Items);
        Assert.Equal(scheduled.Id, Assert.Single(response.RemovedLessons).Id);
    }

    [Fact]
    public async Task UpdateSeries_EndDateTightened_KeepsCompletedHistoryAndEarlierSnapshots()
    {
        // Arrange
        var series = AddSeries(start: new DateOnly(2026, 6, 1));
        var past = AddRow(series, new DateOnly(2026, 6, 29));
        // An individually edited lesson before the new end: rescheduled and repriced.
        var edited = AddRow(series, new DateOnly(2026, 7, 13), price: 250m, startHourUtc: 17, customized: true);
        var completed = AddRow(series, new DateOnly(2026, 7, 27), status: LessonStatus.Completed);
        var scheduled = AddRow(series, new DateOnly(2026, 8, 3));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, new DateOnly(2026, 7, 20)));

        // Assert
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Equal(scheduled.Id, Assert.Single(response.RemovedLessons).Id);
        Assert.DoesNotContain(scheduled, _lessons.Items);
        // A completed lesson already happened and carries its money — it is history, not a plan.
        Assert.Contains(completed, _lessons.Items);
        Assert.Contains(past, _lessons.Items);
        // Shortening the window never rewrites a snapshot taken before it.
        Assert.Equal(250m, edited.Price);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 17, 0, 0, TimeSpan.Zero), edited.StartUtc);
    }

    [Fact]
    public async Task UpdateSeries_PatternCollidingWithAnExistingLesson_ReturnsConflictAndSavesNothing()
    {
        // Arrange
        var series = AddSeries();
        AddRow(series, FirstMonday);
        AddRow(series, new DateOnly(2026, 7, 13));
        // A one-off booked on the very Tuesday slot the new schedule would take.
        AddOneOff(new DateTimeOffset(2026, 7, 7, 15, 0, 0, TimeSpan.Zero));

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, null, Weekdays: Weekdays.Tuesday));

        // Assert
        // The window the new pattern exposes is checked before anything is written, so a refused edit
        // leaves the rule, its lessons and the unit of work exactly as they were.
        Assert.IsType<Conflict<LessonConflictResponse>>(result.Result);
        Assert.Equal(Weekdays.Monday, series.Pattern.Days);
        Assert.Equal(3, _lessons.Items.Count);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_ArchivedStudentsEndedSeries_ReturnsValidationProblemAndKeepsItEnded()
    {
        // Arrange
        // Exactly what archiving leaves behind: the student is archived and the cascade ended their
        // series as of yesterday, so it can produce nothing.
        var student = AddStudent(StudentStatus.Archived);
        var series = AddSeries(studentId: student.Id);
        // Through the rule's own tightening seam: ending a series as of today puts its last day
        // BEFORE its start, which the factory would (rightly) refuse to build from scratch.
        series.End(new DateOnly(2026, 7, 5));

        // Act — the edit that would re-open the rule.
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, null, ClearEndDate: true));

        // Assert
        // Re-opening it would refill their schedule on the spot and have the nightly extender keep
        // it filled, with no second archiving to undo that — so the rule stays stopped.
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("StudentId", problem.Errors.Keys);
        Assert.Equal(new DateOnly(2026, 7, 5), series.EndDate);
        Assert.Empty(_lessons.Items);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateSeries_StudentStillActive_AppliesTheEdit()
    {
        // Arrange
        var student = AddStudent(StudentStatus.Active);
        var series = AddSeries(end: new DateOnly(2026, 7, 20), studentId: student.Id);

        // Act
        var result = await Update(series.Id, new UpdateLessonSeriesRequest(null, null, null, ClearEndDate: true));

        // Assert — the refusal above is about the archived status alone; a student still taught
        // keeps the full editor, open-ended windows included.
        var response = Assert.IsType<Ok<UpdateSeriesResponse>>(result.Result).Value!;
        Assert.Null(response.Series.EndDate);
        Assert.Equal(WeekdaysInHorizon, _lessons.Items.Count);
    }

    [Fact]
    public async Task UpdateSeries_UnknownSeries_ReturnsNotFound()
    {
        // Arrange
        AddSeries();

        // Act
        var result = await Update(Guid.NewGuid(), new UpdateLessonSeriesRequest(null, null, null));

        // Assert
        Assert.IsType<NotFound>(result.Result);
    }

    private LessonSeries AddSeries(DateOnly? start = null, DateOnly? end = null, Guid? studentId = null)
    {
        var series = LessonSeries.Create(
            studentId ?? StudentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            start ?? FirstMonday, CreatedAt, title: "Math", endDate: end, price: 100m).Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        return series;
    }

    /// <summary>A physical row of one slot of the series — generated, or edited by hand.</summary>
    private Lesson AddRow(
        LessonSeries series,
        DateOnly occurrenceDate,
        decimal price = 100m,
        int startHourUtc = 15,
        LessonStatus status = LessonStatus.Scheduled,
        bool customized = false)
    {
        var lesson = Lesson.Create(
            StudentId,
            new DateTimeOffset(occurrenceDate.ToDateTime(new TimeOnly(startHourUtc, 0)), TimeSpan.Zero),
            60, price, CreatedAt, seriesId: series.Id, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        if (customized)
            lesson.MarkCustomized();
        _lessons.Items.Add(lesson);
        return lesson;
    }

    /// <summary>A student of the same tutor, in the status the test needs them.</summary>
    private Student AddStudent(StudentStatus status)
    {
        var student = Student.Create("Kid", 300m, CreatedAt).Value.OwnedBy(Tutor);
        student.ChangeStatus(status);
        _students.Items.Add(student);
        return student;
    }

    /// <summary>A single lesson of the same tutor, belonging to no series.</summary>
    private Lesson AddOneOff(DateTimeOffset startUtc)
    {
        var lesson = Lesson.Create(StudentId, startUtc, 60, 100m, CreatedAt).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private Task<Results<Ok<UpdateSeriesResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> Update(
        Guid seriesId,
        UpdateLessonSeriesRequest request) =>
        Endpoints.UpdateSeries(seriesId, request, _service, default);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
