using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Reports;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Core.Tenancy;
using StudyScheduler.Tests.Features.Lessons;
using StudyScheduler.Tests.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Features.Reports;

/// <summary>
/// End-to-end coverage of <c>GET /reports/dashboard</c> over in-memory repositories: query
/// validation, the period resolved in the tutor's own time zone, the all-time debt ledger and the
/// lessons a series contributes to the window. The arithmetic itself is pinned by
/// <see cref="ReportDashboardServiceTests"/>.
/// </summary>
public class DashboardEndpointTests
{
    private const long Tutor = 777;
    private const long OtherTutor = 778;

    // Berlin is UTC+2 in July/August, so its local day starts at 22:00 UTC the day before.
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static readonly DateTimeOffset CreatedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Wednesday of the week 2026-07-06 .. 2026-07-12, mid-morning.
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly TutorContext _tenant = new();
    private readonly FakeStudentRepository _students;
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeTutorProfileRepository _profiles;

    public DashboardEndpointTests()
    {
        // Every figure below is read through the scope's tenant; the dashboard is never asked whose.
        _tenant.SetFromAuthentication(Tutor);
        _students = new FakeStudentRepository(_tenant);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _profiles = new FakeTutorProfileRepository(_tenant);
    }

    [Fact]
    public async Task GetDashboard_UnknownPeriod_ReturnsValidationProblemForPeriod()
    {
        // Arrange
        AddProfile();

        // Act
        var result = await Get("year");

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.True(problem.Errors.ContainsKey("Period"));
    }

    [Fact]
    public async Task GetDashboard_MissingPeriod_ReturnsValidationProblemForPeriod()
    {
        // Arrange
        AddProfile();

        // Act
        var result = await Get(period: null);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.True(problem.Errors.ContainsKey("Period"));
    }

    [Theory]
    [InlineData("08-07-2026")]
    [InlineData("2026-7-8")]
    [InlineData("tomorrow")]
    public async Task GetDashboard_MalformedAnchor_ReturnsValidationProblemForAnchor(string anchor)
    {
        // Arrange
        AddProfile();

        // Act
        var result = await Get("week", anchor);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.True(problem.Errors.ContainsKey("Anchor"));
    }

    [Fact]
    public async Task GetDashboard_TutorWithoutAProfile_ReturnsValidationProblemForProfile()
    {
        // Arrange — the window boundaries are local dates, so there is no zone to resolve them in.
        var student = AddStudent("Ann");
        AddLesson(student.Id, Local(7, 6, 10), LessonStatus.Completed, isPaid: true);

        // Act
        var result = await Get("week");

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.True(problem.Errors.ContainsKey("Profile"));
    }

    [Fact]
    public async Task GetDashboard_AnchorInsideAWeek_ReturnsThatMondayToSundayWindow()
    {
        // Arrange
        AddProfile();

        // Act — 2026-07-08 is a Wednesday.
        var result = await Get("week", "2026-07-08");

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Equal(new DateOnly(2026, 7, 6), dashboard.Period.From);
        Assert.Equal(new DateOnly(2026, 7, 12), dashboard.Period.To);
    }

    [Fact]
    public async Task GetDashboard_WithoutAnAnchorJustAfterLocalMidnight_ResolvesThePeriodInTheTutorsTimeZone()
    {
        // Arrange — 22:30 UTC on 31 July is already 00:30 on 1 August in Berlin, so "this month"
        // is August; resolving in UTC would have answered July.
        AddProfile();
        var justAfterLocalMidnight = new DateTimeOffset(2026, 7, 31, 22, 30, 0, TimeSpan.Zero);

        // Act
        var result = await Get("month", now: justAfterLocalMidnight);

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Equal(new DateOnly(2026, 8, 1), dashboard.Period.From);
        Assert.Equal(new DateOnly(2026, 8, 31), dashboard.Period.To);
    }

    [Fact]
    public async Task GetDashboard_WithoutAnAnchorJustBeforeLocalMidnight_StaysInTheOutgoingPeriod()
    {
        // Arrange — the same instant one hour earlier is still 31 July in Berlin.
        AddProfile();
        var justBeforeLocalMidnight = new DateTimeOffset(2026, 7, 31, 21, 30, 0, TimeSpan.Zero);

        // Act
        var result = await Get("month", now: justBeforeLocalMidnight);

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Equal(new DateOnly(2026, 7, 1), dashboard.Period.From);
        Assert.Equal(new DateOnly(2026, 7, 31), dashboard.Period.To);
    }

    [Fact]
    public async Task GetDashboard_UnpaidCompletedLessonLongBeforeThePeriod_StillCountedAsDebt()
    {
        // Arrange — a debt does not expire because the reporting window moved on.
        AddProfile();
        var ann = AddStudent("Ann");
        AddLesson(ann.Id, new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero), LessonStatus.Completed, price: 250m);
        AddLesson(ann.Id, new DateTimeOffset(2026, 4, 6, 10, 0, 0, TimeSpan.Zero), LessonStatus.Completed, price: 150m);

        // Act
        var result = await Get("week", "2026-07-08");

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        var debtor = Assert.Single(dashboard.Debt.Debtors);
        Assert.Equal(400m, dashboard.Debt.Total);
        Assert.Equal("Ann", debtor.Name);
        Assert.Equal(2, debtor.LessonsCount);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero), debtor.OldestUtc);
    }

    [Fact]
    public async Task GetDashboard_PaidAndCancelledLessons_AreNeverDebt()
    {
        // Arrange
        AddProfile();
        var ann = AddStudent("Ann");
        AddLesson(ann.Id, Local(7, 6, 10), LessonStatus.Completed, price: 200m, isPaid: true);
        AddLesson(ann.Id, Local(7, 7, 10), LessonStatus.Cancelled, price: 300m);
        AddLesson(ann.Id, Local(7, 9, 10), price: 400m);

        // Act
        var result = await Get("week", "2026-07-08");

        // Assert — only unpaid *completed* lessons are owed.
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Empty(dashboard.Debt.Debtors);
        Assert.Equal(0m, dashboard.Debt.Total);
    }

    [Fact]
    public async Task GetDashboard_Debtors_OrderedByAmountDescending()
    {
        // Arrange
        AddProfile();
        var ann = AddStudent("Ann");
        var bob = AddStudent("Bob");
        AddLesson(ann.Id, Local(7, 6, 10), LessonStatus.Completed, price: 200m);
        AddLesson(bob.Id, Local(7, 7, 10), LessonStatus.Completed, price: 900m);

        // Act
        var result = await Get("week", "2026-07-08");

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Equal(["Bob", "Ann"], dashboard.Debt.Debtors.Select(d => d.Name));
    }

    [Fact]
    public async Task GetDashboard_PaidLessonsInThePrecedingWeek_ReportedAsPreviousIncome()
    {
        // Arrange
        AddProfile();
        var ann = AddStudent("Ann");
        AddLesson(ann.Id, Local(7, 6, 10), LessonStatus.Completed, price: 300m, isPaid: true);
        AddLesson(ann.Id, Local(6, 30, 10), LessonStatus.Completed, price: 500m, isPaid: true);

        // Act
        var result = await Get("week", "2026-07-08");

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Equal(300m, dashboard.Income.Actual);
        Assert.Equal(500m, dashboard.Income.Previous);
    }

    [Fact]
    public async Task GetDashboard_SeriesLessons_CountedLikeAnyOtherLesson()
    {
        // Arrange — Mondays and Wednesdays at 16:00 Berlin from 2026-07-06: two lessons in that week,
        // written out by the series that generated them.
        AddProfile();
        var ann = AddStudent("Ann");
        AddWeeklySeries(ann.Id, new DateOnly(2026, 7, 6), price: 120m);

        // Act
        var result = await Get("week", "2026-07-08");

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Equal(2, dashboard.Lessons.Scheduled);
        Assert.Equal(240m, dashboard.Income.Expected);
        Assert.Equal(0m, dashboard.Income.Actual);
        Assert.Equal(2.0m, dashboard.WeeklyLoad.Hours);
    }

    [Fact]
    public async Task GetDashboard_PerStudent_ExcludesStudentsWhoPaidNothingInThePeriod()
    {
        // Arrange
        AddProfile();
        var ann = AddStudent("Ann");
        var bob = AddStudent("Bob");
        AddLesson(ann.Id, Local(7, 6, 10), LessonStatus.Completed, price: 300m, isPaid: true);
        AddLesson(bob.Id, Local(7, 7, 10), LessonStatus.Completed, price: 900m);

        // Act
        var result = await Get("week", "2026-07-08");

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        var earner = Assert.Single(dashboard.PerStudent);
        Assert.Equal(ann.Id, earner.StudentId);
        Assert.Equal("Ann", earner.Name);
        Assert.Equal(300m, earner.Income);
    }

    [Fact]
    public async Task GetDashboard_AnotherTutorsLessons_AreNeverIncluded()
    {
        // Arrange
        AddProfile();
        var mine = AddStudent("Ann");
        AddLesson(mine.Id, Local(7, 6, 10), LessonStatus.Completed, price: 300m, isPaid: true);

        // Seeded as another tutor's rows: nothing above the database assigns ownership any more, so
        // the fixture plays the part persistence plays.
        var theirs = Student.Create("Someone else", 100m, CreatedAt).Value.OwnedBy(OtherTutor);
        _students.Items.Add(theirs);
        _lessons.Items.Add(
            Lesson.Create(theirs.Id, Local(7, 7, 10), 60, 999m, CreatedAt).Value.OwnedBy(OtherTutor));

        // Act
        var result = await Get("week", "2026-07-08");

        // Assert
        var dashboard = Assert.IsType<Ok<DashboardResponse>>(result.Result).Value!;
        Assert.Equal(1, dashboard.Lessons.Completed);
        Assert.Equal(0, dashboard.Lessons.Scheduled);
        Assert.Equal(300m, dashboard.Income.Expected);
    }

    private Task<Results<Ok<DashboardResponse>, ValidationProblem>> Get(
        string? period,
        string? anchor = null,
        DateTimeOffset? now = null) =>
        Endpoints.GetDashboard(period, anchor, Service(now ?? Now), default);

    private ReportDashboardService Service(DateTimeOffset now) =>
        new(
            _profiles,
            _lessons,
            new FakeStudentDebtReader(_lessons),
            _students,
            _tenant,
            new FixedClock(now),
            NullLogger<ReportDashboardService>.Instance);

    /// <summary>The UTC instant of a 2026 wall clock in Berlin (UTC+2 in summer).</summary>
    private static DateTimeOffset Local(int month, int day, int hourLocal) =>
        new(2026, month, day, hourLocal - 2, 0, 0, TimeSpan.Zero);

    private void AddProfile() => _profiles.Add(TutorProfile.Create(Tutor, Berlin, CreatedAt).Value);

    private Student AddStudent(string name)
    {
        var student = Student.Create(name, 100m, CreatedAt).Value;
        _students.Add(student);
        return student;
    }

    private void AddLesson(
        Guid studentId,
        DateTimeOffset startUtc,
        LessonStatus status = LessonStatus.Scheduled,
        decimal price = 100m,
        bool isPaid = false)
    {
        var lesson = Lesson.Create(studentId, startUtc, 60, price, CreatedAt).Value;
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        if (isPaid)
            lesson.SetPaid(true);

        _lessons.Add(lesson);
    }

    /// <summary>A weekly series together with the week of lessons it generates.</summary>
    private void AddWeeklySeries(Guid studentId, DateOnly startDate, decimal price)
    {
        var pattern = WeeklyPattern.Create(
            Weekdays.Monday | Weekdays.Wednesday, new TimeOnly(16, 0), 60, Berlin).Value;
        var series = LessonSeries.Create(studentId, pattern, startDate, CreatedAt, price: price).Value;
        _series.Add(series);

        foreach (var occurrence in series.GetOccurrences(startDate, startDate.AddDays(6)))
        {
            _lessons.Add(Lesson.Create(
                studentId, occurrence.StartUtc, 60, price, CreatedAt,
                seriesId: series.Id, occurrenceDate: occurrence.OccurrenceDate).Value);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
