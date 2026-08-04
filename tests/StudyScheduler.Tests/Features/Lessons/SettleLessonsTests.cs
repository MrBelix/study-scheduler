using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// The bulk settle behind the debts screen: <see cref="LessonService.SettleAsync"/> takes a selection
/// of lessons and marks them paid in ONE save, or refuses the whole selection and writes nothing.
/// Payment flips through the same domain mutator a single PATCH uses, so a series occurrence comes
/// out latched exactly as it would there.
/// </summary>
public class SettleLessonsTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 777;
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // 2026-07-06 is a Monday; "now" is that morning, so the June lessons below are history.
    private static readonly DateOnly Monday = new(2026, 7, 6);
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonService _sut;

    public SettleLessonsTests()
    {
        // Whose lessons the ids name is the scope's tenant, exactly as in a request.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _sut = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now));
    }

    [Fact]
    public async Task SettleAsync_UnpaidCompletedLessons_MarksThemAllPaidInOneSave()
    {
        // Arrange
        var first = AddCompleted(day: 8);
        var second = AddCompleted(day: 15);

        // Act
        var result = await _sut.SettleAsync([first.Id, second.Id]);

        // Assert
        // The tutor was handed the money once, so the ledger moves once too.
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.True(first.IsPaid);
        Assert.True(second.IsPaid);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task SettleAsync_SeriesOccurrence_LatchesIsCustomized()
    {
        // Arrange
        var occurrence = AddCompleted(day: 8, series: AddSeries());

        // Act
        var result = await _sut.SettleAsync([occurrence.Id]);

        // Assert
        // The very latch a single PATCH sets when it settles the same row: a payment is a per-lesson
        // fact the schedule generator must never regenerate away.
        Assert.True(result.IsSuccess);
        Assert.True(occurrence.IsPaid);
        Assert.True(occurrence.IsCustomized);
    }

    [Fact]
    public async Task SettleAsync_OneOffLesson_LeavesIsCustomizedUnset()
    {
        // Arrange
        var lesson = AddCompleted(day: 8);

        // Act
        var result = await _sut.SettleAsync([lesson.Id]);

        // Assert
        // A one-off is nobody's generation candidate; the flag would say nothing about it.
        Assert.True(result.IsSuccess);
        Assert.False(lesson.IsCustomized);
    }

    [Fact]
    public async Task SettleAsync_LessonAlreadyPaid_CountsItAsSettled()
    {
        // Arrange
        var paid = AddCompleted(day: 8, isPaid: true);
        var unpaid = AddCompleted(day: 15);

        // Act — the retry of a request that already went through, plus one new row.
        var result = await _sut.SettleAsync([paid.Id, unpaid.Id]);

        // Assert
        // Pressing the button twice is not an error: what the client asked for is true either way.
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.True(paid.IsPaid);
        Assert.True(unpaid.IsPaid);
    }

    [Fact]
    public async Task SettleAsync_TheSameIdTwice_SettlesOneLesson()
    {
        // Arrange
        var lesson = AddCompleted(day: 8);

        // Act
        var result = await _sut.SettleAsync([lesson.Id, lesson.Id]);

        // Assert
        // One id names one lesson however often it is sent, so the count is of lessons, not of items.
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.True(lesson.IsPaid);
    }

    [Fact]
    public async Task SettleAsync_UnknownId_RefusesTheWholeBatchWithoutSaving()
    {
        // Arrange
        var lesson = AddCompleted(day: 8);

        // Act
        var result = await _sut.SettleAsync([lesson.Id, Guid.NewGuid()]);

        // Assert
        // All or nothing: the tutor must never end up with half a payment recorded.
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lesson.NotFound", error.Code);
        Assert.Equal("LessonIds", error.Field);
        Assert.False(lesson.IsPaid);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task SettleAsync_AnotherTutorsLesson_RefusesItAsUnknown()
    {
        // Arrange
        var mine = AddCompleted(day: 8);
        var theirs = Lesson.Create(Guid.NewGuid(), Utc(day: 8), 60, 200m, CreatedAt).Value.OwnedBy(OtherTutor);
        theirs.ChangeStatus(LessonStatus.Completed);
        _lessons.Items.Add(theirs);

        // Act
        var result = await _sut.SettleAsync([mine.Id, theirs.Id]);

        // Assert
        // The lookup is tenant-scoped, so their row is not "forbidden" — it simply is not there, and
        // nothing of theirs (or of mine) moves.
        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.NotFound", Assert.Single(result.Errors).Code);
        Assert.False(theirs.IsPaid);
        Assert.False(mine.IsPaid);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Theory]
    [InlineData(LessonStatus.Scheduled)]
    [InlineData(LessonStatus.Cancelled)]
    public async Task SettleAsync_LessonThatWasNotTaught_RefusesTheWholeBatch(LessonStatus status)
    {
        // Arrange
        var taught = AddCompleted(day: 8);
        var other = AddLesson(day: 15, status);

        // Act
        var result = await _sut.SettleAsync([taught.Id, other.Id]);

        // Assert
        // Only a lesson that happened can be owed for: a cancelled one owes nothing and a scheduled
        // one is not owed yet.
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lesson.NotCompleted", error.Code);
        Assert.Equal("LessonIds", error.Field);
        Assert.False(taught.IsPaid);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task SettleAsync_EmptySelection_RefusesWithoutReadingAnything()
    {
        // Arrange
        var lesson = AddCompleted(day: 8);

        // Act
        var result = await _sut.SettleAsync([]);

        // Assert
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lesson.NoneSelected", error.Code);
        Assert.Equal("LessonIds", error.Field);
        Assert.False(lesson.IsPaid);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task Settle_ValidSelection_ReturnsHowManyWereSettled()
    {
        // Arrange
        var first = AddCompleted(day: 8);
        var second = AddCompleted(day: 15);

        // Act
        var result = await Endpoints.Settle(
            new SettleLessonsRequest([first.Id, second.Id]), _sut, default);

        // Assert
        var response = Assert.IsType<Ok<SettleLessonsResponse>>(result.Result).Value!;
        Assert.Equal(2, response.Settled);
    }

    [Fact]
    public async Task Settle_UnknownId_ReturnsValidationProblemOnLessonIds()
    {
        // Arrange
        var lesson = AddCompleted(day: 8);

        // Act
        var result = await Endpoints.Settle(
            new SettleLessonsRequest([lesson.Id, Guid.NewGuid()]), _sut, default);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("LessonIds", problem.Errors.Keys);
    }

    [Fact]
    public async Task Settle_BodyWithoutLessonIds_ReturnsValidationProblem()
    {
        // Arrange
        AddCompleted(day: 8);

        // Act — a body that omits the field reads as an empty selection.
        var result = await Endpoints.Settle(new SettleLessonsRequest(null), _sut, default);

        // Assert
        var problem = Assert.IsType<ValidationProblem>(result.Result).ProblemDetails;
        Assert.Contains("LessonIds", problem.Errors.Keys);
        Assert.Equal(0, _uow.SaveCount);
    }

    /// <summary>A weekly Monday series of the student, the rows below can be generated from.</summary>
    private LessonSeries AddSeries()
    {
        var series = LessonSeries.Create(
            StudentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            Monday, CreatedAt, title: "Algebra").Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        return series;
    }

    /// <summary>A lesson of June <paramref name="day"/> already recorded as taught.</summary>
    private Lesson AddCompleted(int day, bool isPaid = false, LessonSeries? series = null)
    {
        var lesson = AddLesson(day, LessonStatus.Completed, series);
        if (isPaid)
            lesson.SetPaid(true);
        return lesson;
    }

    private Lesson AddLesson(int day, LessonStatus status, LessonSeries? series = null)
    {
        var lesson = Lesson.Create(
            StudentId, Utc(day), 60, 200m, CreatedAt,
            seriesId: series?.Id, occurrenceDate: series is null ? null : new DateOnly(2026, 6, day))
            .Value.OwnedBy(Tutor);
        lesson.ChangeStatus(status);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private static DateTimeOffset Utc(int day) => new(2026, 6, day, 15, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
