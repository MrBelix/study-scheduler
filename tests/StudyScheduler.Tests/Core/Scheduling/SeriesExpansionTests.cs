using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Features.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Core.Scheduling;

public class SeriesExpansionTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // Mon/Thu 16:00 London (BST → 15:00 UTC in July).
    private static readonly DateTimeOffset From = new(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly SeriesExpansion _sut;

    public SeriesExpansionTests() => _sut = new SeriesExpansion(_lessons, _series);

    private LessonSeries AddSeries(Guid? studentId = null, DateOnly? start = null, DateOnly? end = null)
    {
        var series = LessonSeries.Create(
            Tutor, studentId ?? Guid.NewGuid(),
            WeeklyPattern.Create(Weekdays.Monday | Weekdays.Thursday, new TimeOnly(16, 0), 60, London).Value,
            start ?? new DateOnly(2026, 7, 6), CreatedAt, endDate: end).Value;
        _series.Items.Add(series);
        return series;
    }

    [Fact]
    public async Task Expands_MatchingOccurrencesInWindow()
    {
        AddSeries();

        var result = await _sut.GetFreeOccurrencesAsync(Tutor, From, To);

        var dates = Assert.Single(result).Occurrences.Select(o => o.OccurrenceDate);
        Assert.Equal(new[] { new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 9) }, dates);
    }

    [Fact]
    public async Task Suppresses_MaterializedSlot()
    {
        var series = AddSeries();
        // A physical row for the Monday slot → that occurrence must not be returned as free.
        _lessons.Items.Add(Lesson.Create(
            Tutor, series.StudentId, new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero), 60, 0m, CreatedAt,
            seriesId: series.Id, occurrenceDate: new DateOnly(2026, 7, 6)).Value);

        var result = await _sut.GetFreeOccurrencesAsync(Tutor, From, To);

        var date = Assert.Single(Assert.Single(result).Occurrences).OccurrenceDate;
        Assert.Equal(new DateOnly(2026, 7, 9), date);
    }

    [Fact]
    public async Task Excludes_SeriesEndedBeforeWindow()
    {
        // A valid past series (starts and ends well before the July window).
        AddSeries(start: new DateOnly(2026, 5, 1), end: new DateOnly(2026, 6, 1));

        Assert.Empty(await _sut.GetFreeOccurrencesAsync(Tutor, From, To));
    }

    [Fact]
    public async Task FiltersByStudent()
    {
        var wanted = Guid.NewGuid();
        AddSeries(studentId: wanted);
        AddSeries(studentId: Guid.NewGuid());

        var result = await _sut.GetFreeOccurrencesAsync(Tutor, From, To, studentId: wanted);

        Assert.Equal(wanted, Assert.Single(result).Series.StudentId);
    }
}
