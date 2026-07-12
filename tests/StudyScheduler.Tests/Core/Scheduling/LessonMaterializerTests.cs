using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Features.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Core.Scheduling;

public class LessonMaterializerTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly FakeStudentRepository _students = new();
    private readonly LessonMaterializer _sut;

    public LessonMaterializerTests() =>
        _sut = new LessonMaterializer(_students, TimeProvider.System, NullLogger<LessonMaterializer>.Instance);

    private (LessonSeries Series, LessonOccurrence Occurrence) BuildSlot(decimal? seriesPrice, Guid studentId)
    {
        var series = LessonSeries.Create(
            Tutor, studentId,
            WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt, price: seriesPrice).Value;
        return (series, series.GetOccurrences(new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 6))[0]);
    }

    [Fact]
    public async Task Materializes_WithSeriesLinkAndOccurrenceTime()
    {
        var (series, occurrence) = BuildSlot(seriesPrice: 500m, studentId: Guid.NewGuid());

        var lesson = await _sut.MaterializeSlotAsync(series, occurrence);

        Assert.Equal(series.Id, lesson.SeriesId);
        Assert.Equal(new DateOnly(2026, 7, 6), lesson.OccurrenceDate);
        Assert.Equal(occurrence.StartUtc, lesson.StartUtc);
        Assert.Equal(60, lesson.DurationMinutes);
        Assert.Equal(500m, lesson.Price); // series' own price wins
    }

    [Fact]
    public async Task ResolvesPrice_FromStudentRate_WhenSeriesPriceNull()
    {
        var student = Student.Create(Tutor, "Bob", 275m, CreatedAt).Value;
        _students.Items.Add(student);
        var (series, occurrence) = BuildSlot(seriesPrice: null, studentId: student.Id);

        var lesson = await _sut.MaterializeSlotAsync(series, occurrence);

        Assert.Equal(275m, lesson.Price);
    }

    [Fact]
    public async Task ResolvesPrice_ToZero_WhenStudentMissing()
    {
        var (series, occurrence) = BuildSlot(seriesPrice: null, studentId: Guid.NewGuid());

        var lesson = await _sut.MaterializeSlotAsync(series, occurrence);

        Assert.Equal(0m, lesson.Price);
    }
}
