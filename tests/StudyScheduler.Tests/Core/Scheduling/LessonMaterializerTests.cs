using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Core.Scheduling;

public class LessonMaterializerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private const long TutorId = 42;

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeStudentRepository _students = new();

    private LessonMaterializer CreateMaterializer() => new(
        _lessons,
        new SeriesExpansion(_lessons, _series),
        _students,
        new FixedTimeProvider(Now),
        NullLogger<LessonMaterializer>.Instance);

    private Student AddStudent(decimal rate = 100m)
    {
        var student = Student.Create(TutorId, "Alice", rate, Now.AddDays(-30)).Value;
        _students.Students.Add(student);
        return student;
    }

    /// <summary>Weekly Mondays at 10:00 UTC, effective 2026-07-01 — occurrences on Jul 6/13/20/27.</summary>
    private LessonSeries AddMondaySeries(Guid studentId, decimal? price = null)
    {
        var series = LessonSeries.Create(
            TutorId, studentId, new DateOnly(2026, 7, 1), Weekdays.Monday,
            new TimeOnly(10, 0), 60, TimeZoneInfo.Utc, Now.AddDays(-30), price: price).Value;
        _series.Series.Add(series);
        return series;
    }

    [Fact]
    public async Task Schedule_merges_physical_and_virtual_slots_ordered_by_start()
    {
        var student = AddStudent();
        AddMondaySeries(student.Id);
        var physical = Lesson.Create(
            TutorId, student.Id, new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero), 60, 100m, Now).Value;
        _lessons.Lessons.Add(physical);

        var schedule = await CreateMaterializer().GetScheduleAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

        // Virtual Monday 13th, physical Wednesday 15th, virtual Monday 20th — sorted by StartUtc.
        Assert.Equal(3, schedule.Count);
        Assert.Equal(
            [
                new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            ],
            schedule.Select(s => s.StartUtc));
        Assert.Equal([true, false, true], schedule.Select(s => s.IsVirtual));
        Assert.Equal(physical.Id, schedule[1].Id);
    }

    [Fact]
    public async Task Materialized_row_rescheduled_outside_the_window_still_suppresses_its_virtual_slot()
    {
        var student = AddStudent();
        var series = AddMondaySeries(student.Id);

        // The Jul 13 occurrence was materialized and then rescheduled past the requested window.
        // The row governs its slot by OccurrenceDate, so neither the (out-of-range) physical
        // lesson nor a phantom virtual slot may show for Jul 13.
        var moved = Lesson.Create(
            TutorId, student.Id, new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero), 60, 100m, Now,
            seriesId: series.Id, occurrenceDate: new DateOnly(2026, 7, 13)).Value;
        _lessons.Lessons.Add(moved);

        var schedule = await CreateMaterializer().GetScheduleAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(schedule);
    }

    [Fact]
    public async Task Series_price_wins_over_the_students_rate()
    {
        var student = AddStudent(rate: 100m);
        AddMondaySeries(student.Id, price: 250m);

        var schedule = await CreateMaterializer().GetScheduleAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(250m, Assert.Single(schedule).Price);
    }

    [Fact]
    public async Task Missing_student_prices_virtual_slots_at_zero_instead_of_failing()
    {
        // The series references a student that is gone from the store — a data anomaly that
        // must not take the whole schedule read down.
        AddMondaySeries(Guid.NewGuid(), price: null);

        var schedule = await CreateMaterializer().GetScheduleAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(0m, Assert.Single(schedule).Price);
    }

    [Fact]
    public async Task StudentId_filter_excludes_other_students_series()
    {
        var mine = AddStudent();
        var other = AddStudent();
        AddMondaySeries(mine.Id);
        AddMondaySeries(other.Id);

        var schedule = await CreateMaterializer().GetScheduleAsync(
            TutorId,
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            studentId: mine.Id);

        Assert.Equal(mine.Id, Assert.Single(schedule).StudentId);
    }
}
