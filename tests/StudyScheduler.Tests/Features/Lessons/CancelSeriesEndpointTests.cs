using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// Endpoint-level coverage for <see cref="Endpoints.CancelSeries"/> when a series is cancelled
/// effective immediately: which of the lessons it generated go with the schedule that ceased to
/// exist — which is what <c>keepCustomized</c> decides — and which stand because they already
/// started or already happened.
/// </summary>
public class CancelSeriesEndpointTests
{
    private const long Tutor = 555;
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // London is on BST (UTC+1) in July, so a 16:00 local weekly slot expands to 15:00 UTC.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday.
    private static readonly DateOnly Monday = new(2026, 7, 6);

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();

    public CancelSeriesEndpointTests()
    {
        // The cancel reaches the series the scope owns; nothing about the request names a tutor.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
    }

    private LessonSeries AddSeries()
    {
        // Price set so a generated row does not depend on a student rate lookup.
        var series = LessonSeries.Create(
            StudentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt, price: 100m).Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        return series;
    }

    private LessonService Service(DateTimeOffset now) =>
        LessonServiceFactory.Create(_tenant, _lessons, _series, _students, _uow, new FixedClock(now));

    /// <summary>A physical row of one slot of the series — generated, or edited by hand.</summary>
    private Lesson AddRow(
        LessonSeries series,
        DateOnly occurrenceDate,
        LessonStatus status = LessonStatus.Scheduled,
        bool customized = false)
    {
        var lesson = Lesson.Create(
            StudentId,
            new DateTimeOffset(occurrenceDate.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero),
            60, 100m, CreatedAt, seriesId: series.Id, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        if (customized)
            lesson.MarkCustomized();
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private Task<Results<Ok<CancelSeriesResponse>, NotFound>> Cancel(
        Guid seriesId,
        LessonService service,
        bool? keepCustomized = null) =>
        Endpoints.CancelSeries(
            seriesId,
            keepCustomized is { } keep ? new CancelLessonSeriesRequest(keep) : null,
            service,
            default);

    [Fact]
    public async Task CancelSeries_TodayLessonAlreadyStarted_IsKept()
    {
        // Arrange
        var series = AddSeries();
        var today = AddRow(series, Monday);
        // Monday 16:00 London = 15:00 UTC; "now" is 16:00 UTC → today's lesson already started.
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);

        // Act
        var result = await Cancel(series.Id, Service(now));

        // Assert
        // The sweep selects by instant, so an in-progress (or finished) lesson is out of its reach and
        // is never silently dropped.
        var response = Assert.IsType<Ok<CancelSeriesResponse>>(result.Result).Value!;
        Assert.Same(today, Assert.Single(_lessons.Items));
        Assert.Empty(response.RemovedLessons);
        // EndDate tightened to yesterday, so the rule produces nothing from today on.
        Assert.Equal(Monday.AddDays(-1), series.EndDate);
    }

    [Fact]
    public async Task CancelSeries_TodayLessonStillUpcoming_IsSweptWithTheSchedule()
    {
        // Arrange
        var series = AddSeries();
        var today = AddRow(series, Monday);
        // "now" is 14:00 UTC, before the 15:00 UTC start → today's lesson has not started.
        var now = new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.Zero);

        // Act
        var result = await Cancel(series.Id, Service(now));

        // Assert
        // "Effective immediately" includes the rest of today: a lesson that has not started belongs to
        // the plan that just ceased to exist.
        var response = Assert.IsType<Ok<CancelSeriesResponse>>(result.Result).Value!;
        Assert.DoesNotContain(today, _lessons.Items);
        Assert.Equal(today.Id, Assert.Single(response.RemovedLessons).Id);
        Assert.Equal(Monday.AddDays(-1), series.EndDate);
    }

    [Fact]
    public async Task CancelSeries_FutureGeneratedLesson_IsRemovedAndReported()
    {
        // Arrange
        var series = AddSeries();
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);
        var planned = AddRow(series, Monday.AddDays(7));

        // Act
        var result = await Cancel(series.Id, Service(now));

        // Assert
        // A lesson beyond today belongs to a schedule that no longer exists: it goes, and it is
        // reported so the client can tell the student.
        var response = Assert.IsType<Ok<CancelSeriesResponse>>(result.Result).Value!;
        Assert.DoesNotContain(planned, _lessons.Items);
        Assert.Equal(planned.Id, Assert.Single(response.RemovedLessons).Id);
    }

    [Fact]
    public async Task CancelSeries_FutureCompletedLesson_IsPreserved()
    {
        // Arrange
        // A lesson on a future slot the tutor already closed out as taught.
        var series = AddSeries();
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);
        var completed = AddRow(series, Monday.AddDays(7), LessonStatus.Completed);

        // Act
        var result = await Cancel(series.Id, Service(now), keepCustomized: false);

        // Assert
        // History outranks the plan whatever the sweep was asked to do: a lesson that happened (and
        // carries its money) stays in the reports and out of the removals.
        var response = Assert.IsType<Ok<CancelSeriesResponse>>(result.Result).Value!;
        Assert.Contains(completed, _lessons.Items);
        Assert.Equal(LessonStatus.Completed, completed.Status);
        Assert.Empty(response.RemovedLessons);
    }

    [Fact]
    public async Task CancelSeries_KeepingCustomized_SparesTheHandEditedLessons()
    {
        // Arrange
        var series = AddSeries();
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);
        var edited = AddRow(series, Monday.AddDays(7), customized: true);
        var generated = AddRow(series, Monday.AddDays(14));

        // Act
        var result = await Cancel(series.Id, Service(now), keepCustomized: true);

        // Assert
        // The default reading of "end this series": the plan stops, but the occurrences the tutor
        // decided something about individually stay on the calendar.
        var response = Assert.IsType<Ok<CancelSeriesResponse>>(result.Result).Value!;
        Assert.Contains(edited, _lessons.Items);
        Assert.DoesNotContain(generated, _lessons.Items);
        Assert.Equal(generated.Id, Assert.Single(response.RemovedLessons).Id);
    }

    [Fact]
    public async Task CancelSeries_NotKeepingCustomized_RemovesTheHandEditedLessonsToo()
    {
        // Arrange
        var series = AddSeries();
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);
        var edited = AddRow(series, Monday.AddDays(7), customized: true);
        var cancelledByHand = AddRow(series, Monday.AddDays(14), LessonStatus.Cancelled, customized: true);

        // Act
        var result = await Cancel(series.Id, Service(now), keepCustomized: false);

        // Assert
        // The explicit "wipe everything ahead" reading: nothing of the plan survives, however it was
        // edited, and every loss is reported.
        var response = Assert.IsType<Ok<CancelSeriesResponse>>(result.Result).Value!;
        Assert.DoesNotContain(edited, _lessons.Items);
        Assert.DoesNotContain(cancelledByHand, _lessons.Items);
        Assert.Equal(2, response.RemovedLessons.Count);
    }

    [Fact]
    public async Task CancelSeries_PastLessons_AreNeverTouched()
    {
        // Arrange
        // The series has been running for weeks; those lessons happened.
        var series = AddSeries();
        var now = new DateTimeOffset(2026, 7, 20, 18, 0, 0, TimeSpan.Zero);
        var past = AddRow(series, Monday);
        var pastCustomized = AddRow(series, Monday.AddDays(7), customized: true);

        // Act
        var result = await Cancel(series.Id, Service(now), keepCustomized: false);

        // Assert
        // "Cancel from now on" means exactly that — a lesson that already started is out of reach of
        // either sweep mode.
        var response = Assert.IsType<Ok<CancelSeriesResponse>>(result.Result).Value!;
        Assert.Contains(past, _lessons.Items);
        Assert.Contains(pastCustomized, _lessons.Items);
        Assert.Empty(response.RemovedLessons);
    }

    [Fact]
    public async Task CancelSeries_UnknownSeries_ReturnsNotFound()
    {
        // Arrange
        AddSeries();
        var now = new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);

        // Act
        var result = await Cancel(Guid.NewGuid(), Service(now));

        // Assert
        Assert.IsType<NotFound>(result.Result);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
