using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Features.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Core.Scheduling;

public class ScheduleReaderTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static readonly DateTimeOffset From = new(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeStudentRepository _students = new();
    private readonly ScheduleReader _sut;

    public ScheduleReaderTests() =>
        _sut = new ScheduleReader(_lessons, new SeriesExpansion(_lessons, _series), _students);

    private Guid AddStudent(decimal rate)
    {
        var student = Student.Create(Tutor, "Bob", rate, CreatedAt).Value;
        _students.Items.Add(student);
        return student.Id;
    }

    private LessonSeries AddSeries(Guid studentId, decimal? price = null)
    {
        var series = LessonSeries.Create(
            Tutor, studentId,
            WeeklyPattern.Create(Weekdays.Monday | Weekdays.Thursday, new TimeOnly(16, 0), 60, London).Value,
            new DateOnly(2026, 7, 6), CreatedAt, price: price).Value;
        _series.Items.Add(series);
        return series;
    }

    [Fact]
    public async Task Merges_PhysicalAndVirtual_OrderedByStart()
    {
        var studentId = AddStudent(rate: 300m);
        AddSeries(studentId);
        // A one-off on Tuesday 10:00–11:00 UTC.
        _lessons.Items.Add(Lesson.Create(Tutor, studentId, new DateTimeOffset(2026, 7, 7, 10, 0, 0, TimeSpan.Zero), 60, 100m, CreatedAt).Value);

        var schedule = await _sut.GetScheduleAsync(Tutor, From, To);

        Assert.Equal(3, schedule.Count); // Mon06 virtual, Tue07 physical, Thu09 virtual
        Assert.True(schedule.SequenceEqual(schedule.OrderBy(e => e.StartUtc))); // sorted
        Assert.Equal(2, schedule.Count(e => e.IsVirtual));
    }

    [Fact]
    public async Task VirtualEntries_UseStudentRate_WhenSeriesPriceNull()
    {
        var studentId = AddStudent(rate: 300m);
        AddSeries(studentId, price: null);

        var schedule = await _sut.GetScheduleAsync(Tutor, From, To);

        Assert.All(schedule, e => Assert.Equal(300m, e.Price));
    }

    [Fact]
    public async Task PhysicalRow_SuppressesItsVirtualSlot()
    {
        var studentId = AddStudent(rate: 300m);
        var series = AddSeries(studentId);
        // Materialize the Monday slot as a physical (cancelled) row.
        var lesson = Lesson.Create(
            Tutor, studentId, new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero), 60, 300m, CreatedAt,
            seriesId: series.Id, occurrenceDate: new DateOnly(2026, 7, 6)).Value;
        lesson.ChangeStatus(LessonStatus.Cancelled);
        _lessons.Items.Add(lesson);

        var schedule = await _sut.GetScheduleAsync(Tutor, From, To);

        // Monday shows once, as the physical row (not virtual); Thursday stays virtual.
        var monday = Assert.Single(schedule, e => e.OccurrenceDate == new DateOnly(2026, 7, 6));
        Assert.False(monday.IsVirtual);
        Assert.Equal(LessonStatus.Cancelled, monday.Status);
        Assert.Contains(schedule, e => e.OccurrenceDate == new DateOnly(2026, 7, 9) && e.IsVirtual);
    }
}
