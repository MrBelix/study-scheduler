using System.Net;
using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>End-to-end tests for the Lessons feature over the real stack (SQL container + API).</summary>
[Collection(nameof(AppCollection))]
public class LessonsTests(AppFixture app)
{
    [Fact]
    public async Task OneOff_created_appears_in_schedule()
    {
        var tutor = TelegramInitData.ForUser(4001, "Al");
        var studentId = await CreateStudent(tutor);
        var start = FutureUtc(days: 3, hour: 10);

        var create = await app.Api.PostAs(tutor, "/lessons", new { studentId, startUtc = start, durationMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lesson = (await create.Content.ReadFromJsonAsync<LessonDto>())!;
        Assert.NotNull(lesson.Id);
        Assert.False(lesson.IsVirtual);

        var schedule = await GetSchedule(tutor, start.AddHours(-1), start.AddHours(2));
        Assert.Contains(schedule, l => l.Id == lesson.Id);
    }

    [Fact]
    public async Task Series_expands_virtual_then_materializes_via_occurrence_patch()
    {
        var tutor = TelegramInitData.ForUser(4002, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);

        var from = DayUtc(monday);
        var to = from.AddDays(1);

        // Before any mutation the day shows a single virtual slot.
        var before = await GetSchedule(tutor, from, to);
        var slot = Assert.Single(before);
        Assert.True(slot.IsVirtual);
        Assert.Null(slot.Id);
        Assert.Equal(series.Id, slot.SeriesId);

        // Touching the slot materializes it.
        var patch = await app.Api.PatchAs(
            tutor, $"/lessons/series/{series.Id}/occurrences/{monday:yyyy-MM-dd}", new { topic = "Algebra" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var materialized = (await patch.Content.ReadFromJsonAsync<LessonDto>())!;
        Assert.NotNull(materialized.Id);
        Assert.False(materialized.IsVirtual);
        Assert.Equal("Algebra", materialized.Topic);

        // Now the schedule serves the physical row and suppresses the virtual slot.
        var after = await GetSchedule(tutor, from, to);
        var entry = Assert.Single(after);
        Assert.False(entry.IsVirtual);
        Assert.Equal(materialized.Id, entry.Id);
    }

    [Fact]
    public async Task Cancel_series_removes_future_overrides_and_reports_them()
    {
        var tutor = TelegramInitData.ForUser(4003, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);

        // Materialize the (future) Monday occurrence, then end the series.
        await app.Api.PatchAs(tutor, $"/lessons/series/{series.Id}/occurrences/{monday:yyyy-MM-dd}", new { topic = "X" });

        var cancel = await app.Api.PostAs(tutor, $"/lessons/series/{series.Id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var result = (await cancel.Content.ReadFromJsonAsync<CancelDto>())!;

        Assert.NotNull(result.Series.EndDate);
        Assert.Contains(result.RemovedLessons, l => l.OccurrenceDate == monday);
    }

    [Fact]
    public async Task Overlapping_one_off_returns_conflict()
    {
        var tutor = TelegramInitData.ForUser(4004, "Al");
        var studentId = await CreateStudent(tutor);
        var start = FutureUtc(days: 4, hour: 10);

        await app.Api.PostAs(tutor, "/lessons", new { studentId, startUtc = start, durationMinutes = 60 });
        var conflict = await app.Api.PostAs(
            tutor, "/lessons", new { studentId, startUtc = start.AddMinutes(30), durationMinutes = 60 });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Create_series_without_profile_returns_bad_request()
    {
        var tutor = TelegramInitData.ForUser(4005, "Al");
        var studentId = await CreateStudent(tutor);

        var resp = await app.Api.PostAs(tutor, "/lessons/series", new
        {
            studentId,
            startDate = NextMonday(),
            weekdays = "Monday",
            startTimeLocal = "16:00:00",
            durationMinutes = 60,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Lesson_of_another_tutor_reads_as_not_found()
    {
        var tutorA = TelegramInitData.ForUser(4006, "Al");
        var tutorB = TelegramInitData.ForUser(4007, "Bo");
        var studentId = await CreateStudent(tutorA);
        var start = FutureUtc(days: 5, hour: 10);
        var lesson = (await (await app.Api.PostAs(
            tutorA, "/lessons", new { studentId, startUtc = start, durationMinutes = 60 }))
            .Content.ReadFromJsonAsync<LessonDto>())!;

        var resp = await app.Api.GetAs(tutorB, $"/lessons/{lesson.Id}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- helpers ----

    private async Task<Guid> CreateStudent(string tutor, decimal rate = 300m)
    {
        var resp = await app.Api.PostAs(tutor, "/students", new { name = "Student", rate });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<StudentDto>())!.Id;
    }

    private async Task SetProfile(string tutor, string zone = "Europe/Kyiv")
    {
        var resp = await app.Api.PutAs(tutor, "/profile", new { timeZoneId = zone });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<SeriesDto> CreateWeeklySeries(string tutor, Guid studentId, DateOnly startMonday)
    {
        var resp = await app.Api.PostAs(tutor, "/lessons/series", new
        {
            studentId,
            startDate = startMonday,
            weekdays = "Monday",
            startTimeLocal = "16:00:00",
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<SeriesDto>())!;
    }

    private async Task<List<LessonDto>> GetSchedule(string tutor, DateTimeOffset from, DateTimeOffset to)
    {
        var url = $"/lessons?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var resp = await app.Api.GetAs(tutor, url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<LessonDto>>())!;
    }

    private static DateTimeOffset FutureUtc(int days, int hour) =>
        new(DateTime.UtcNow.Date.AddDays(days).AddHours(hour), TimeSpan.Zero);

    private static DateTimeOffset DayUtc(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>A Monday comfortably in the future (so occurrences are unambiguously "after today").</summary>
    private static DateOnly NextMonday()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        while (date.DayOfWeek != DayOfWeek.Monday)
            date = date.AddDays(1);
        return date;
    }

    private sealed record LessonDto(
        Guid? Id, Guid StudentId, Guid? SeriesId, DateOnly? OccurrenceDate,
        DateTimeOffset StartUtc, DateTimeOffset EndUtc, int DurationMinutes, string Status,
        decimal Price, bool IsPaid, string? Topic, string? Description, bool IsVirtual, DateTimeOffset CreatedAtUtc);

    private sealed record SeriesDto(
        Guid Id, Guid StudentId, string? Title, DateOnly StartDate, DateOnly? EndDate, string Weekdays,
        TimeOnly StartTimeLocal, int DurationMinutes, string TimeZoneId, decimal? Price, DateTimeOffset CreatedAtUtc);

    private sealed record CancelDto(SeriesDto Series, List<LessonDto> RemovedLessons);

    private sealed record StudentDto(Guid Id, string Name, decimal Rate, string Status, DateTimeOffset CreatedAtUtc);
}
