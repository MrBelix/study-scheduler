using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// The generation half of the pivot: a series is a rule that WRITES rows, filling the four-month
/// planning horizon and never touching a date that already has a row of its own.
/// </summary>
public class LessonGeneratorTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // 2026-07-06 is a Monday; London is on BST (UTC+1) in July, so a 16:00 local slot is 15:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateOnly Monday = new(2026, 7, 6);

    // Four months from 2026-07-06 lands on a Friday, so the last Monday inside the window is Nov 2.
    private static readonly DateOnly Horizon = new(2026, 11, 6);
    private static readonly DateOnly LastMondayInHorizon = new(2026, 11, 2);
    private const int MondaysInHorizon = 18;

    private readonly RecordingTutorScope _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();
    private readonly Guid _studentId;

    public LessonGeneratorTests()
    {
        // The repositories read through the very scope the pass drives, so what the generator can see
        // moves with the tenant it puts itself into.
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);

        // The pass starts tenant-less, so the fixture rows are stamped the way persistence stamps them.
        var student = Student.Create("Ann", 300m, CreatedAt).Value.OwnedBy(Tutor);
        _students.Items.Add(student);
        _studentId = student.Id;
    }

    [Fact]
    public async Task GenerateAsync_OverAFreshWindow_StagesOneRowPerOccurrence()
    {
        // Arrange
        var sut = GeneratorAsTutor();
        var series = AddSeries();

        // Act
        var staged = await sut.GenerateAsync(series, Monday, Horizon);

        // Assert
        Assert.Equal(MondaysInHorizon, staged.Count);
        Assert.Equal(MondaysInHorizon, _lessons.Items.Count);
        Assert.All(_lessons.Items, l => Assert.Equal(series.Id, l.SeriesId));
        Assert.All(_lessons.Items, l => Assert.Equal(LessonStatus.Scheduled, l.Status));
        Assert.All(_lessons.Items, l => Assert.False(l.IsCustomized));
        Assert.Equal(Monday, _lessons.Items[0].OccurrenceDate);
        Assert.Equal(LastMondayInHorizon, _lessons.Items[^1].OccurrenceDate);
    }

    [Fact]
    public async Task GenerateAsync_RunTwiceOverTheSameWindow_StagesNothingTheSecondTime()
    {
        // Arrange
        var sut = GeneratorAsTutor();
        var series = AddSeries();
        await sut.GenerateAsync(series, Monday, Horizon);

        // Act
        var staged = await sut.GenerateAsync(series, Monday, Horizon);

        // Assert
        // Idempotence is what lets the nightly pass re-cover ground it already filled.
        Assert.Empty(staged);
        Assert.Equal(MondaysInHorizon, _lessons.Items.Count);
    }

    [Fact]
    public async Task GenerateAsync_DateHoldingACancelledCustomizedRow_LeavesThatDateAlone()
    {
        // Arrange
        // The user cancelled this single occurrence. Regeneration must not resurrect it as a fresh
        // Scheduled lesson — the row owns its date from now on.
        var sut = GeneratorAsTutor();
        var series = AddSeries();
        var cancelled = AddRow(series, Monday);
        cancelled.ChangeStatus(LessonStatus.Cancelled);
        cancelled.MarkCustomized();

        // Act
        var staged = await sut.GenerateAsync(series, Monday, Horizon);

        // Assert
        Assert.Equal(MondaysInHorizon - 1, staged.Count);
        Assert.Equal(MondaysInHorizon, _lessons.Items.Count);
        var onMonday = Assert.Single(_lessons.Items, l => l.OccurrenceDate == Monday);
        Assert.Same(cancelled, onMonday);
        Assert.Equal(LessonStatus.Cancelled, onMonday.Status);
    }

    [Fact]
    public async Task GenerateAsync_DateHoldingAnUntouchedGeneratedRow_LeavesThatDateAlone()
    {
        // Arrange — customization is not the point: ANY existing row owns its date.
        var sut = GeneratorAsTutor();
        var series = AddSeries();
        AddRow(series, Monday.AddDays(7));

        // Act
        var staged = await sut.GenerateAsync(series, Monday, Horizon);

        // Assert
        Assert.Equal(MondaysInHorizon - 1, staged.Count);
        Assert.Single(_lessons.Items, l => l.OccurrenceDate == Monday.AddDays(7));
    }

    [Fact]
    public async Task GenerateAsync_SeriesWithItsOwnPrice_SnapshotsThatPrice()
    {
        // Arrange
        var sut = GeneratorAsTutor();
        var series = AddSeries(price: 450m);

        // Act
        await sut.GenerateAsync(series, Monday, Monday);

        // Assert
        Assert.Equal(450m, Assert.Single(_lessons.Items).Price);
    }

    [Fact]
    public async Task GenerateAsync_SeriesWithoutAPrice_SnapshotsTheStudentRate()
    {
        // Arrange
        var sut = GeneratorAsTutor();
        var series = AddSeries(price: null);

        // Act
        await sut.GenerateAsync(series, Monday, Monday);

        // Assert
        // The snapshot rule: series price ?? student rate ?? 0.
        Assert.Equal(300m, Assert.Single(_lessons.Items).Price);
    }

    [Fact]
    public async Task GenerateAsync_SeriesWhoseStudentIsGone_SnapshotsAZeroPrice()
    {
        // Arrange
        var sut = GeneratorAsTutor();
        var series = AddSeries(price: null);
        _students.Items.Clear();

        // Act
        await sut.GenerateAsync(series, Monday, Monday);

        // Assert
        // A data anomaly must not take a whole generation pass down.
        var lesson = Assert.Single(_lessons.Items);
        Assert.Equal(0m, lesson.Price);
        Assert.True(lesson.IsPaid);
    }

    [Fact]
    public async Task GenerateAsync_WindowRunningPastTheSeriesEndDate_StopsAtTheEndDate()
    {
        // Arrange
        var sut = GeneratorAsTutor();
        var series = AddSeries(endDate: Monday.AddDays(14));

        // Act
        var staged = await sut.GenerateAsync(series, Monday, Horizon);

        // Assert
        Assert.Equal(3, staged.Count);
        Assert.Equal(Monday.AddDays(14), _lessons.Items[^1].OccurrenceDate);
    }

    [Fact]
    public async Task GenerateAsync_WindowEndingBeforeTheSeriesStarts_StagesNothing()
    {
        // Arrange
        var sut = GeneratorAsTutor();
        var series = AddSeries();

        // Act
        var staged = await sut.GenerateAsync(series, Monday.AddDays(-14), Monday.AddDays(-1));

        // Assert
        Assert.Empty(staged);
        Assert.Empty(_lessons.Items);
    }

    [Fact]
    public async Task GenerateAsync_AcrossTheAutumnDstTransition_KeepsTheLocalWallClock()
    {
        // Arrange
        // London goes back to GMT on 2026-10-25, between these two Mondays. The 16:00 lesson must stay
        // at 16:00 local, which means its UTC instant shifts by an hour.
        var start = new DateOnly(2026, 10, 19);
        var sut = GeneratorAsTutor();
        var series = AddSeries(startDate: start, endDate: new DateOnly(2026, 10, 26));

        // Act
        var staged = await sut.GenerateAsync(series, start, new DateOnly(2026, 10, 26));

        // Assert
        Assert.Equal(2, staged.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.Zero), _lessons.Items[0].StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero), _lessons.Items[1].StartUtc);
        Assert.All(_lessons.Items, l => Assert.Equal(60, l.DurationMinutes));
    }

    [Fact]
    public async Task ExtendAllAsync_SeriesPredatingEagerGeneration_BackfillsTheWholeWindow()
    {
        // Arrange
        // A series created back in January that never wrote a row: the first nightly tick is its
        // backfill. Only the window from today on is filled; the past stays as it is.
        var sut = Generator();
        AddSeries(startDate: new DateOnly(2026, 1, 5));

        // Act
        var generated = await sut.ExtendAllAsync();

        // Assert
        Assert.Equal(MondaysInHorizon, generated);
        Assert.Equal(Monday, _lessons.Items[0].OccurrenceDate);
        Assert.Equal(LastMondayInHorizon, _lessons.Items[^1].OccurrenceDate);
    }

    [Fact]
    public async Task ExtendAllAsync_SeriesEndingInsideTheWindow_StopsAtItsEndDate()
    {
        // Arrange
        var sut = Generator();
        AddSeries(endDate: Monday.AddDays(21));

        // Act
        var generated = await sut.ExtendAllAsync();

        // Assert
        Assert.Equal(4, generated);
        Assert.Equal(Monday.AddDays(21), _lessons.Items[^1].OccurrenceDate);
    }

    [Fact]
    public async Task ExtendAllAsync_SeriesThatAlreadyEnded_GeneratesNothing()
    {
        // Arrange
        var sut = Generator();
        AddSeries(startDate: new DateOnly(2026, 1, 5), endDate: new DateOnly(2026, 3, 30));

        // Act
        var generated = await sut.ExtendAllAsync();

        // Assert
        Assert.Equal(0, generated);
        Assert.Empty(_lessons.Items);
    }

    [Fact]
    public async Task ExtendAllAsync_RunTwice_GeneratesNothingTheSecondTime()
    {
        // Arrange
        var sut = Generator();
        AddSeries();
        await sut.ExtendAllAsync();

        // Act
        var generated = await sut.ExtendAllAsync();

        // Assert
        Assert.Equal(0, generated);
        Assert.Equal(MondaysInHorizon, _lessons.Items.Count);
    }

    [Fact]
    public async Task ExtendAllAsync_OneSeriesFailingToCommit_StillGeneratesTheRest()
    {
        // Arrange
        // The failing series is the older one, so it is walked first (candidates come oldest first).
        var uow = new FailFirstSaveUnitOfWork(_lessons);
        var sut = Generator(uow);
        AddSeries(createdAtUtc: CreatedAt);
        var healthy = AddSeries(
            weekdays: Weekdays.Tuesday, startDate: Monday.AddDays(1), createdAtUtc: CreatedAt.AddHours(1));

        // Act
        var generated = await sut.ExtendAllAsync();

        // Assert
        // The doomed rows are discarded rather than replayed into the next series' save, so exactly
        // one series' worth of lessons survives the tick.
        Assert.Equal(_lessons.Items.Count, generated);
        Assert.NotEmpty(_lessons.Items);
        Assert.All(_lessons.Items, l => Assert.Equal(healthy.Id, l.SeriesId));
    }

    [Fact]
    public async Task ExtendAllAsync_SeriesOfSeveralTutors_GeneratesForEachUnderItsOwnTenant()
    {
        // Arrange
        // The nightly pass is tenant-less by nature: it must reach EVERY tutor in one tick, which it
        // does by borrowing each series' owner as the tenant of the reads and writes that follow.
        const long otherTutor = 777;
        var sut = Generator();
        var mine = AddSeries(createdAtUtc: CreatedAt);
        var theirs = AddSeries(
            weekdays: Weekdays.Tuesday,
            startDate: Monday.AddDays(1),
            createdAtUtc: CreatedAt.AddHours(1),
            tutorId: otherTutor);

        // Act
        var generated = await sut.ExtendAllAsync();

        // Assert
        Assert.Equal(new[] { Tutor, otherTutor }, _tenant.Tenants);
        Assert.Contains(_lessons.Items, l => l.SeriesId == mine.Id && l.TutorTelegramId == Tutor);
        Assert.Contains(_lessons.Items, l => l.SeriesId == theirs.Id && l.TutorTelegramId == otherTutor);
        Assert.Equal(_lessons.Items.Count, generated);
    }

    private LessonGenerator Generator(IUnitOfWork? uow = null) => new(
        _lessons, _series, _students, uow ?? _uow, _tenant, new FixedClock(Now),
        NullLogger<LessonGenerator>.Instance);

    /// <summary>
    /// The generator inside a tenant's scope, which is the only way its single-series entry point is
    /// ever reached: a request that just created or edited the series, or the nightly pass having
    /// borrowed the owner of the series it is about to fill.
    /// </summary>
    private LessonGenerator GeneratorAsTutor()
    {
        _tenant.SetForBackground(Tutor);
        return Generator();
    }

    /// <summary>A weekly 16:00 London series of this tutor, with no rows behind it yet.</summary>
    private LessonSeries AddSeries(
        Weekdays weekdays = Weekdays.Monday,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        decimal? price = 300m,
        DateTimeOffset? createdAtUtc = null,
        long tutorId = Tutor)
    {
        var series = LessonSeries.Create(
            _studentId,
            WeeklyPattern.Create(weekdays, new TimeOnly(16, 0), 60, London).Value,
            startDate ?? Monday, createdAtUtc ?? CreatedAt, endDate: endDate, price: price).Value.OwnedBy(tutorId);
        _series.Items.Add(series);
        return series;
    }

    /// <summary>A physical row already claiming one slot of <paramref name="series"/>.</summary>
    private Lesson AddRow(LessonSeries series, DateOnly occurrenceDate)
    {
        var occurrence = series.GetOccurrences(occurrenceDate, occurrenceDate)[0];
        var lesson = Lesson.Create(
            _studentId, occurrence.StartUtc, 60, 300m, CreatedAt,
            seriesId: series.Id, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Fails its FIRST save and behaves like the real change tracker on discard: everything staged
    /// since the last successful commit is dropped, so a doomed batch cannot leak into a later one.
    /// </summary>
    private sealed class FailFirstSaveUnitOfWork(FakeLessonRepository lessons) : IUnitOfWork
    {
        private List<Lesson> _committed = [];
        private bool _failed;

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            if (!_failed)
            {
                _failed = true;
                return Task.FromException(new InvalidOperationException("Save failed."));
            }

            _committed = [.. lessons.Items];
            return Task.CompletedTask;
        }

        public void DiscardChanges()
        {
            lessons.Items.Clear();
            lessons.Items.AddRange(_committed);
        }
    }
}
