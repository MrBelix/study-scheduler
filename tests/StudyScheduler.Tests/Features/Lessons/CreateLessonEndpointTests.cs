using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Core.Tenancy;
using StudyScheduler.Tests.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// Endpoint-level coverage for the one create route: the same form makes a single lesson or a
/// weekly series depending on <c>Repeat</c>, both branches read one local wall clock resolved
/// through the tutor's profile zone, and the response says which of the two was created.
/// </summary>
public class CreateLessonEndpointTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 777;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // London is on GMT in January and BST (UTC+1) in July, and switches inside a March/October week
    // — the whole DST vocabulary in one zone.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday; a 16:00 local slot there expands to 15:00 UTC.
    private static readonly DateOnly FirstMonday = new(2026, 7, 6);
    private static readonly DateTimeOffset FirstMondayStartUtc = new(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);

    // "Today" is 2026-07-01 in London, so a hand-placed lesson may reach 2026-11-01 and no further.
    private static readonly DateOnly LastPlannableDate = new(2026, 11, 1);

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeTutorProfileRepository _profiles;
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonService _service;
    private readonly Guid _studentId;

    public CreateLessonEndpointTests()
    {
        // The request's identity is the scope's tenant; from here on nothing names a tutor.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _profiles = new FakeTutorProfileRepository(_tenant);

        // Added through the repository, so the scope stamps the owner exactly as SaveChanges does.
        var student = Student.Create("Ann", 300m, CreatedAt).Value;
        _students.Add(student);
        _studentId = student.Id;

        _profiles.Add(TutorProfile.Create(Tutor, London, CreatedAt).Value);

        _service = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now), _profiles);
    }

    [Theory]
    [InlineData("2026-01-12", "16:00", "2026-01-12T16:00:00Z")] // GMT — local is UTC
    [InlineData("2026-07-06", "16:00", "2026-07-06T15:00:00Z")] // BST — one hour ahead of UTC
    [InlineData("2026-03-29", "01:30", "2026-03-29T01:30:00Z")] // spring-forward week: 01:30 does not
                                                                // exist, so it is shifted to 02:30 BST
    [InlineData("2026-10-25", "01:30", "2026-10-25T01:30:00Z")] // fall-back week: the hour happens twice
                                                                // and the standard (GMT) offset wins
    public async Task Create_WithoutRepeat_ResolvesTheLocalWallClockThroughTheProfileZone(
        string date, string startTimeLocal, string expectedStartUtc)
    {
        // Arrange
        var request = Request(
            date: DateOnly.Parse(date, CultureInfo.InvariantCulture),
            startTimeLocal: TimeOnly.Parse(startTimeLocal, CultureInfo.InvariantCulture));

        // Act
        var result = await Create(request);

        // Assert
        // The client sends no instant at all: the profile zone is what turns "that day at that time"
        // into a moment, exactly as it does for a series' occurrences.
        var lesson = Assert.IsType<Created<CreateLessonResponse>>(result.Result).Value!.Lesson!;
        Assert.Equal(Parse(expectedStartUtc), lesson.StartUtc);
        Assert.Equal(Parse(expectedStartUtc).AddMinutes(60), lesson.EndUtc);
    }

    [Fact]
    public async Task Create_WithoutRepeat_ReturnsTheLessonHalfOfTheResponse()
    {
        // Arrange
        var request = Request(topic: "Past simple");

        // Act
        var result = await Create(request);

        // Assert
        // Exactly one half is filled in, so the client switches on the payload instead of on a route.
        var created = Assert.IsType<Created<CreateLessonResponse>>(result.Result);
        var response = created.Value!;
        Assert.NotNull(response.Lesson);
        Assert.Null(response.Series);

        var lesson = Assert.Single(_lessons.Items);
        Assert.Equal(lesson.Id, response.Lesson!.Id);
        Assert.Equal($"/lessons/{lesson.Id}", created.Location);
        Assert.Equal("Past simple", response.Lesson.Topic);
        Assert.Equal(_studentId, response.Lesson.StudentId);
        Assert.Null(response.Lesson.SeriesId);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task Create_WithoutRepeatAndWithoutPrice_SnapshotsTheStudentRate()
    {
        // Arrange
        var request = Request(price: null);

        // Act
        var result = await Create(request);

        // Assert
        var response = Assert.IsType<Created<CreateLessonResponse>>(result.Result).Value!;
        Assert.Equal(300m, response.Lesson!.Price);
    }

    [Fact]
    public async Task Create_WithoutRepeatBeyondThePlanningHorizon_ReturnsValidationProblem()
    {
        // Arrange — one day past the four months the calendar is planned for.
        var request = Request(date: LastPlannableDate.AddDays(1));

        // Act
        var result = await Create(request);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("Date", problem.Errors.Keys);
        Assert.Empty(_lessons.Items);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Create_WithoutRepeatOnTheLastDayOfThePlanningHorizon_CreatesTheLesson()
    {
        // Arrange — the edge itself is inside the window.
        var request = Request(date: LastPlannableDate);

        // Act
        var result = await Create(request);

        // Assert
        Assert.IsType<Created<CreateLessonResponse>>(result.Result);
        Assert.Single(_lessons.Items);
    }

    [Fact]
    public async Task Create_WithRepeat_ReturnsTheSeriesHalfOfTheResponse()
    {
        // Arrange
        var request = Request(
            price: 450m,
            repeat: new LessonRepeatRequest(
                Weekdays.Monday | Weekdays.Thursday, "Math", new DateOnly(2026, 8, 31)));

        // Act
        var result = await Create(request);

        // Assert
        var created = Assert.IsType<Created<CreateLessonResponse>>(result.Result);
        var response = created.Value!;
        Assert.Null(response.Lesson);
        Assert.NotNull(response.Series);

        var series = Assert.Single(_series.Items);
        Assert.Equal(series.Id, response.Series!.Id);
        Assert.Equal($"/lessons/series/{series.Id}", created.Location);
        // The request's date is the day the schedule takes effect, and its time and duration are the
        // weekly pattern's — anchored in the profile zone.
        Assert.Equal(FirstMonday, response.Series.StartDate);
        Assert.Equal(new DateOnly(2026, 8, 31), response.Series.EndDate);
        Assert.Equal(Weekdays.Monday | Weekdays.Thursday, response.Series.Weekdays);
        Assert.Equal(new TimeOnly(16, 0), response.Series.StartTimeLocal);
        Assert.Equal(60, response.Series.DurationMinutes);
        Assert.Equal(London.Id, response.Series.TimeZoneId);
        Assert.Equal("Math", response.Series.Title);
        Assert.Equal(450m, response.Series.Price);
        Assert.Equal(Now, response.Series.CreatedAtUtc);
    }

    [Fact]
    public async Task Create_WithRepeat_GeneratesThePlanningWindowInTheSameRequest()
    {
        // Arrange
        var request = Request(repeat: new LessonRepeatRequest(Weekdays.Monday, null, null));

        // Act
        var result = await Create(request);

        // Assert
        // A series is a generation rule, and its rows exist before the response is written: every
        // Monday from the start date to the four-month horizon (2026-07-06 … 2026-11-06, whose last
        // Monday is 2026-11-02) — 18 of them. Open-ended does not mean endless: the nightly extender
        // rolls the window forward from here.
        Assert.IsType<Created<CreateLessonResponse>>(result.Result);
        var series = Assert.Single(_series.Items);
        Assert.Equal(18, _lessons.Items.Count);
        Assert.All(_lessons.Items, l => Assert.Equal(series.Id, l.SeriesId));
        // Nothing is customized yet — every row is still purely a product of the rule.
        Assert.All(_lessons.Items, l => Assert.False(l.IsCustomized));
        Assert.Equal(FirstMonday, _lessons.Items[0].OccurrenceDate);
        Assert.Equal(FirstMondayStartUtc, _lessons.Items[0].StartUtc);
        Assert.Equal(new DateOnly(2026, 11, 2), _lessons.Items[^1].OccurrenceDate);
    }

    [Fact]
    public async Task Create_WithRepeatEndingInsideTheHorizon_GeneratesUpToTheEndDateOnly()
    {
        // Arrange
        var request = Request(repeat: new LessonRepeatRequest(
            Weekdays.Monday, null, new DateOnly(2026, 8, 31)));

        // Act
        var result = await Create(request);

        // Assert
        // The window is min(start + 4 months, endDate): nine Mondays, the last one being the end date.
        Assert.IsType<Created<CreateLessonResponse>>(result.Result);
        Assert.Equal(9, _lessons.Items.Count);
        Assert.Equal(new DateOnly(2026, 8, 31), _lessons.Items[^1].OccurrenceDate);
    }

    [Fact]
    public async Task Create_WithRepeatAndWithoutPrice_SnapshotsTheStudentRateOnEveryGeneratedLesson()
    {
        // Arrange
        var request = Request(
            price: null,
            repeat: new LessonRepeatRequest(Weekdays.Monday, null, new DateOnly(2026, 7, 20)));

        // Act
        var result = await Create(request);

        // Assert
        // The generator's snapshot rule: the series' price, else the student's rate.
        Assert.IsType<Created<CreateLessonResponse>>(result.Result);
        Assert.Equal(3, _lessons.Items.Count);
        Assert.All(_lessons.Items, l => Assert.Equal(300m, l.Price));
    }

    [Fact]
    public async Task Create_WithRepeatAndNoWeekdays_ReturnsValidationProblem()
    {
        // Arrange
        var request = Request(repeat: new LessonRepeatRequest(Weekdays.None, null, null));

        // Act
        var result = await Create(request);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("Weekdays", problem.Errors.Keys);
        Assert.Empty(_series.Items);
    }

    [Fact]
    public async Task Create_WithRepeatEndingBeforeTheStartDate_ReturnsValidationProblem()
    {
        // Arrange
        var request = Request(repeat: new LessonRepeatRequest(
            Weekdays.Monday, null, FirstMonday.AddDays(-1)));

        // Act
        var result = await Create(request);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("EndDate", problem.Errors.Keys);
        Assert.Empty(_series.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_WithoutATutorProfile_ReturnsValidationProblem(bool repeating)
    {
        // Arrange
        // Both branches read a local wall clock, so neither can place a lesson without the zone the
        // tutor scheduled in — including the single lesson, which used to send its own instant.
        _profiles.Items.Clear();
        var request = Request(repeat: repeating ? new LessonRepeatRequest(Weekdays.Monday, null, null) : null);

        // Act
        var result = await Create(request);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("Profile", problem.Errors.Keys);
        Assert.Empty(_lessons.Items);
        Assert.Empty(_series.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_ForAStudentOfAnotherTutor_ReturnsValidationProblem(bool repeating)
    {
        // Arrange
        // A student that really exists — for somebody else. The lookup behind the create path is
        // tenant-filtered, so it comes back null exactly as a missing id would, and the request is
        // refused on StudentId rather than leaking that the row is there.
        var theirs = Student.Create("Their kid", 300m, CreatedAt).Value.OwnedBy(OtherTutor);
        _students.Items.Add(theirs);
        var request = Request(repeat: repeating ? new LessonRepeatRequest(Weekdays.Monday, null, null) : null)
            with { StudentId = theirs.Id };

        // Act
        var result = await Create(request);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("StudentId", problem.Errors.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_ForAnArchivedStudent_ReturnsValidationProblem(bool repeating)
    {
        // Arrange
        // Archiving them said the tutor stopped teaching them, and emptied their schedule to match.
        var archived = Student.Create("Former kid", 300m, CreatedAt).Value;
        archived.ChangeStatus(StudentStatus.Archived);
        _students.Add(archived);
        var request = Request(repeat: repeating ? new LessonRepeatRequest(Weekdays.Monday, null, null) : null)
            with { StudentId = archived.Id };

        // Act
        var result = await Create(request);

        // Assert
        // Both branches of the one form are refused on StudentId, and nothing is written: booking for
        // an archived student would contradict the very invariant archiving establishes.
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("StudentId", problem.Errors.Keys);
        Assert.Empty(_lessons.Items);
        Assert.Empty(_series.Items);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Create_WithoutRepeatOverAnExistingLesson_ReturnsConflict()
    {
        // Arrange
        var existing = AddOneOff(FirstMondayStartUtc);
        var request = Request(startTimeLocal: new TimeOnly(16, 30));

        // Act
        var result = await Create(request);

        // Assert
        var conflict = Assert.IsType<Conflict<LessonConflictResponse>>(result.Result).Value!;
        Assert.Equal(existing.Id, Assert.Single(conflict.Conflicts).LessonId);
        // Refused before anything was written.
        Assert.Single(_lessons.Items);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Create_WithoutRepeatOverAGeneratedSeriesLesson_ReturnsConflict()
    {
        // Arrange
        // A series occupies its slots through the lessons it generated — that row is what a
        // hand-placed lesson runs into.
        var series = AddSeries();
        var generated = AddSeriesLesson(series, FirstMonday);
        var request = Request(startTimeLocal: new TimeOnly(16, 30));

        // Act
        var result = await Create(request);

        // Assert
        var conflict = Assert.Single(Assert.IsType<Conflict<LessonConflictResponse>>(result.Result).Value!.Conflicts);
        Assert.Equal(series.Id, conflict.SeriesId);
        Assert.Equal(generated.Id, conflict.LessonId);
        // Refused before anything was written: the generated lesson is all there is.
        Assert.Single(_lessons.Items);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Create_WithRepeatCollidingWithAnotherSeries_ReturnsConflict()
    {
        // Arrange
        var other = AddSeries();
        var request = Request(
            startTimeLocal: new TimeOnly(16, 30),
            repeat: new LessonRepeatRequest(Weekdays.Monday, null, null));

        // Act
        var result = await Create(request);

        // Assert
        var conflict = Assert.IsType<Conflict<LessonConflictResponse>>(result.Result).Value!;
        Assert.Equal(other.Id, Assert.Single(conflict.Conflicts).SeriesId);
        Assert.Single(_series.Items);
        Assert.Equal(0, _uow.SaveCount);
        // Nothing was generated either: the refusal comes before the series is written.
        Assert.Empty(_lessons.Items);
    }

    [Fact]
    public async Task Create_WithRepeatCollidingWithAnExistingLesson_ReturnsConflict()
    {
        // Arrange
        // A single lesson booked on one of the weekly slots the new schedule would claim.
        var existing = AddOneOff(FirstMondayStartUtc.AddDays(7));
        var request = Request(repeat: new LessonRepeatRequest(Weekdays.Monday, null, null));

        // Act
        var result = await Create(request);

        // Assert
        var conflict = Assert.IsType<Conflict<LessonConflictResponse>>(result.Result).Value!;
        Assert.Equal(existing.Id, Assert.Single(conflict.Conflicts).LessonId);
        Assert.Empty(_series.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_WithAnImpossibleDuration_ReturnsValidationProblem(bool repeating)
    {
        // Arrange
        var request = Request(
            durationMinutes: 5,
            repeat: repeating ? new LessonRepeatRequest(Weekdays.Monday, null, null) : null);

        // Act
        var result = await Create(request);

        // Assert
        // One vocabulary, one rule: the domain's duration bounds answer for both branches.
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("DurationMinutes", problem.Errors.Keys);
        Assert.Empty(_lessons.Items);
        Assert.Empty(_series.Items);
    }

    private static DateTimeOffset Parse(string utc) =>
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private CreateLessonRequest Request(
        DateOnly? date = null,
        TimeOnly? startTimeLocal = null,
        int durationMinutes = 60,
        decimal? price = null,
        string? topic = null,
        LessonRepeatRequest? repeat = null) =>
        new(_studentId, date ?? FirstMonday, startTimeLocal ?? new TimeOnly(16, 0),
            durationMinutes, price, topic, repeat);

    /// <summary>A lesson already on the calendar, used as the thing a new request runs into.</summary>
    private Lesson AddOneOff(DateTimeOffset startUtc)
    {
        var lesson = Lesson.Create(_studentId, startUtc, 60, 300m, CreatedAt).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    /// <summary>A weekly Monday 16:00 series in the tutor's own zone, with no lessons of its own yet.</summary>
    private LessonSeries AddSeries()
    {
        var series = LessonSeries.Create(
            _studentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            FirstMonday, CreatedAt, price: 300m).Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        return series;
    }

    /// <summary>The row the generator writes for one slot of the series.</summary>
    private Lesson AddSeriesLesson(LessonSeries series, DateOnly occurrenceDate)
    {
        var occurrence = series.GetOccurrences(occurrenceDate, occurrenceDate)[0];
        var lesson = Lesson.Create(
            series.StudentId, occurrence.StartUtc, 60, 300m, CreatedAt,
            seriesId: series.Id, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private Task<Results<Created<CreateLessonResponse>, ValidationProblem, Conflict<LessonConflictResponse>>> Create(
        CreateLessonRequest request) =>
        Endpoints.Create(request, _service, default);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
