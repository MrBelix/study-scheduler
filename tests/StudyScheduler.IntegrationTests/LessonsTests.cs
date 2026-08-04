using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace StudyScheduler.IntegrationTests;

/// <summary>End-to-end tests for the Lessons feature over the real stack (PostgreSQL container + API).</summary>
[Collection(nameof(AppCollection))]
public class LessonsTests(AppFixture app)
{
    /// <summary>The zone every tutor here schedules in — the one POST /lessons resolves local times with.</summary>
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    [Fact]
    public async Task OneOff_created_appears_in_schedule()
    {
        var tutor = TelegramInitData.ForUser(4001, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var date = FutureDate(days: 3);

        var created = await CreateOneOff(tutor, studentId, date);
        Assert.NotEqual(Guid.Empty, created.Id);

        var schedule = await GetScheduleAround(tutor, date);
        Assert.Contains(schedule, l => l.Id == created.Id);
    }

    [Fact]
    public async Task Series_creation_generates_the_planning_window_of_lessons()
    {
        const long tutorId = 4014;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();

        var series = await CreateWeeklySeries(tutor, studentId, monday);

        // The rows exist the moment the series does: no read, no patch, no background job needed.
        await using var db = app.CreateDbContext(tutorId);
        var rows = await db.Lessons
            .AsNoTracking()
            .Where(l => l.SeriesId == series.Id)
            .OrderBy(l => l.OccurrenceDate)
            .ToListAsync();

        // Every Monday from the start date to four months out, and not one beyond.
        var horizon = monday.AddMonths(4);
        var expected = 0;
        for (var date = monday; date <= horizon; date = date.AddDays(7))
            expected++;

        Assert.Equal(expected, rows.Count);
        Assert.Equal(monday, rows[0].OccurrenceDate);
        Assert.True(rows[^1].OccurrenceDate <= horizon);
        Assert.All(rows, r => Assert.False(r.IsCustomized));
        Assert.All(rows, r => Assert.Equal(300m, r.Price));

        // And GET /lessons serves them under their own row ids.
        var schedule = await GetSchedule(tutor, DayUtc(monday), DayUtc(monday.AddDays(1)));
        var entry = Assert.Single(schedule);
        Assert.Equal(rows[0].Id, entry.Id);
        Assert.Equal(series.Id, entry.SeriesId);
        Assert.Equal(monday, entry.OccurrenceDate);
    }

    [Fact]
    public async Task Series_lesson_answers_to_one_id_from_the_list_to_the_patch()
    {
        var tutor = TelegramInitData.ForUser(4002, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);

        var from = DayUtc(monday);
        var to = from.AddDays(1);

        // Before any mutation the day shows exactly one lesson, under its own id.
        var before = await GetSchedule(tutor, from, to);
        var slot = Assert.Single(before);
        Assert.Equal(series.Id, slot.SeriesId);
        var id = slot.Id;

        // Touching the lesson patches it; the response is addressed by the very same id.
        var patch = await app.Api.PatchAs(tutor, $"/lessons/{id}", new { topic = "Algebra" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = (await patch.Content.ReadFromJsonAsync<LessonDto>())!;
        Assert.Equal(id, patched.Id);
        Assert.Equal("Algebra", patched.Topic);

        // The schedule still serves one entry for the day — same id again.
        var after = await GetSchedule(tutor, from, to);
        var entry = Assert.Single(after);
        Assert.Equal(id, entry.Id);
        Assert.Equal("Algebra", entry.Topic);

        // And the id resolves directly.
        var read = await app.Api.GetAs(tutor, $"/lessons/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(id, (await read.Content.ReadFromJsonAsync<LessonDto>())!.Id);
    }

    [Fact]
    public async Task Patching_a_series_lesson_marks_it_customized()
    {
        const long tutorId = 4015;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);

        var patch = await app.Api.PatchAs(
            tutor, $"/lessons/{await LessonIdOn(tutor, monday)}", new { topic = "Algebra" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // The latch is what keeps a hand-edited occurrence safe from any later regeneration.
        await using var db = app.CreateDbContext(tutorId);
        var rows = await db.Lessons
            .AsNoTracking()
            .Where(l => l.SeriesId == series.Id)
            .ToListAsync();
        Assert.True(rows.Single(l => l.OccurrenceDate == monday).IsCustomized);
        Assert.All(rows.Where(l => l.OccurrenceDate != monday), r => Assert.False(r.IsCustomized));
    }

    [Fact]
    public async Task Reading_a_lesson_by_its_id_writes_nothing()
    {
        const long tutorId = 4010;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);
        var id = await LessonIdOn(tutor, monday);

        await using var db = app.CreateDbContext(tutorId);
        var before = await db.Lessons.AsNoTracking().CountAsync(l => l.SeriesId == series.Id);

        var read = await app.Api.GetAs(tutor, $"/lessons/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(id, (await read.Content.ReadFromJsonAsync<LessonDto>())!.Id);

        // A read never writes: the rows the create request generated are all there are.
        Assert.Equal(before, await db.Lessons.AsNoTracking().CountAsync(l => l.SeriesId == series.Id));
    }

    [Fact]
    public async Task Unknown_lesson_id_returns_not_found()
    {
        var tutor = TelegramInitData.ForUser(4019, "Al");

        // An id nobody ever wrote: there is nothing to project a lesson from any more.
        var unknown = await app.Api.GetAs(tutor, $"/lessons/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // And a segment that is not a GUID matches no route at all — routing answers it
        // before any handler is chosen.
        var malformed = await app.Api.GetAs(tutor, "/lessons/not-an-id");
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Series_routes_are_not_shadowed_by_the_lesson_id_route()
    {
        var tutor = TelegramInitData.ForUser(4012, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var series = await CreateWeeklySeries(tutor, studentId, NextMonday());

        // The literal "series" segment outranks {id:guid}, which "series" could never match anyway.
        var list = await app.Api.GetAs(tutor, "/lessons/series");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains((await list.Content.ReadFromJsonAsync<List<SeriesDto>>())!, s => s.Id == series.Id);

        var one = await app.Api.GetAs(tutor, $"/lessons/series/{series.Id}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
    }

    [Fact]
    public async Task Concurrent_occurrence_patches_touch_exactly_one_row()
    {
        const long tutorId = 4009;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);
        var url = $"/lessons/{await LessonIdOn(tutor, monday)}";

        // Two requests race for the same lesson. Both patch the one row the unique
        // (SeriesId, OccurrenceDate) index allows — neither may fail or duplicate it.
        var responses = await Task.WhenAll(
            app.Api.PatchAs(tutor, url, new { topic = "A" }),
            app.Api.PatchAs(tutor, url, new { topic = "B" }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        // The HTTP surface can only ever show one row for the slot, so assert on the table itself.
        await using var db = app.CreateDbContext(tutorId);
        var rows = await db.Lessons
            .AsNoTracking()
            .Where(l => l.SeriesId == series.Id && l.OccurrenceDate == monday)
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task Cancel_series_not_keeping_customized_removes_every_future_lesson()
    {
        const long tutorId = 4003;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);

        // Individually edit the (future) Monday occurrence, then end the series sweeping everything.
        var edited = await LessonIdOn(tutor, monday);
        await app.Api.PatchAs(tutor, $"/lessons/{edited}", new { topic = "X" });

        var cancel = await app.Api.PostAs(
            tutor, $"/lessons/series/{series.Id}/cancel", new { keepCustomized = false });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var result = (await cancel.Content.ReadFromJsonAsync<CancelDto>())!;

        Assert.NotNull(result.Series.EndDate);
        Assert.Contains(result.RemovedLessons, l => l.Id == edited);

        // The whole generated window is gone with the schedule it came from.
        await using var db = app.CreateDbContext(tutorId);
        Assert.Empty(await db.Lessons.AsNoTracking().Where(l => l.SeriesId == series.Id).ToListAsync());
    }

    [Fact]
    public async Task Cancel_series_without_a_body_keeps_the_hand_edited_lesson()
    {
        const long tutorId = 4018;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);

        var edited = await LessonIdOn(tutor, monday);
        await app.Api.PatchAs(tutor, $"/lessons/{edited}", new { topic = "X" });

        // No body at all: the default reading of the route, and the one every existing client sends.
        var cancel = await app.Api.PostAs(tutor, $"/lessons/series/{series.Id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var result = (await cancel.Content.ReadFromJsonAsync<CancelDto>())!;

        Assert.DoesNotContain(result.RemovedLessons, l => l.Id == edited);

        // The occurrence the tutor decided something about is all that survives the cancellation.
        await using var db = app.CreateDbContext(tutorId);
        var rows = await db.Lessons.AsNoTracking().Where(l => l.SeriesId == series.Id).ToListAsync();
        var kept = Assert.Single(rows);
        Assert.Equal(monday, kept.OccurrenceDate);
        Assert.True(kept.IsCustomized);
    }

    [Fact]
    public async Task End_series_by_date_drops_the_lessons_beyond_it()
    {
        var tutor = TelegramInitData.ForUser(4013, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);
        var lastDay = monday.AddDays(7);

        // An individually edited lesson beyond the date the series is about to stop at.
        var edited = await LessonIdOn(tutor, monday.AddDays(14));
        await app.Api.PatchAs(tutor, $"/lessons/{edited}", new { topic = "X" });

        var update = await app.Api.PatchAs(
            tutor, $"/lessons/series/{series.Id}", new { endDate = lastDay, keepCustomized = false });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var result = (await update.Content.ReadFromJsonAsync<UpdateDto>())!;

        Assert.Equal(lastDay, result.Series.EndDate);
        Assert.Contains(result.RemovedLessons, l => l.Id == edited);

        // The cut is exact: the last day still shows, the week after it no longer does.
        var kept = await GetSchedule(tutor, DayUtc(lastDay), DayUtc(lastDay.AddDays(1)));
        Assert.Equal(lastDay, Assert.Single(kept).OccurrenceDate);
        Assert.Empty(await GetSchedule(tutor, DayUtc(lastDay.AddDays(7)), DayUtc(lastDay.AddDays(8))));
    }

    [Fact]
    public async Task End_series_by_date_with_a_new_price_reprices_the_lessons_it_keeps()
    {
        const long tutorId = 4020;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);
        var lastDay = monday.AddDays(7);

        // One edit that both tightens the window and raises the price: the sweep deletes what falls
        // outside it while the repricing rewrites what stays — over the very same rows, in one commit.
        var update = await app.Api.PatchAs(
            tutor, $"/lessons/series/{series.Id}", new { endDate = lastDay, price = 500m });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var result = (await update.Content.ReadFromJsonAsync<UpdateDto>())!;
        Assert.Equal(500m, result.Series.Price);

        await using var db = app.CreateDbContext(tutorId);
        var rows = await db.Lessons
            .AsNoTracking()
            .Where(l => l.SeriesId == series.Id)
            .OrderBy(l => l.OccurrenceDate)
            .ToListAsync();

        // The cut still happened — repricing must not put a swept row back on the calendar.
        Assert.Equal([monday, lastDay], rows.Select(r => r.OccurrenceDate));
        // And nothing is left behind on the 300 it was generated at.
        Assert.All(rows, r => Assert.Equal(500m, r.Price));
    }

    [Fact]
    public async Task OneOff_local_time_is_resolved_in_the_profile_zone_and_stored_as_an_instant()
    {
        // The client sends a wall clock, never an instant: the profile zone turns it into one, and
        // PostgreSQL timestamptz stores that plain moment (Npgsql refuses a non-zero offset).
        var tutor = TelegramInitData.ForUser(4008, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var date = FutureDate(days: 6);
        var time = new TimeOnly(10, 0);

        var created = await CreateOneOff(tutor, studentId, date, time);

        var read = await app.Api.GetAs(tutor, $"/lessons/{created.Id}");
        var lesson = (await read.Content.ReadFromJsonAsync<LessonDto>())!;
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), Kyiv), lesson.StartUtc.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, lesson.StartUtc.Offset);
    }

    [Fact]
    public async Task OneOff_beyond_the_planning_horizon_returns_bad_request()
    {
        var tutor = TelegramInitData.ForUser(4016, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);

        // Comfortably past four months whichever way the tutor's zone rounds "today".
        var resp = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date = FutureDate(days: 0).AddMonths(4).AddDays(3),
            startTimeLocal = "10:00:00",
            durationMinutes = 60,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Overlapping_one_off_returns_conflict()
    {
        var tutor = TelegramInitData.ForUser(4004, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var date = FutureDate(days: 4);

        await CreateOneOff(tutor, studentId, date, new TimeOnly(10, 0));
        var conflict = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date,
            startTimeLocal = "10:30:00",
            durationMinutes = 60,
        });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Repeating_lesson_without_profile_returns_bad_request()
    {
        var tutor = TelegramInitData.ForUser(4005, "Al");
        var studentId = await CreateStudent(tutor);

        var resp = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date = NextMonday(),
            startTimeLocal = "16:00:00",
            durationMinutes = 60,
            repeat = new { weekdays = "Monday" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Lesson_of_another_tutor_reads_as_not_found()
    {
        var tutorA = TelegramInitData.ForUser(4006, "Al");
        var tutorB = TelegramInitData.ForUser(4007, "Bo");
        await SetProfile(tutorA);
        var studentId = await CreateStudent(tutorA);
        var lesson = await CreateOneOff(tutorA, studentId, FutureDate(days: 5));

        var resp = await app.Api.GetAs(tutorB, $"/lessons/{lesson.Id}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Edit_series_schedule_regenerates_the_future_lessons()
    {
        const long tutorId = 4017;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        var series = await CreateWeeklySeries(tutor, studentId, monday);
        var editedDate = monday.AddDays(7);
        var firstMondayId = await LessonIdOn(tutor, monday);

        // One occurrence the tutor decided something about: it must survive the regeneration.
        var editedId = await LessonIdOn(tutor, editedDate);
        var patch = await app.Api.PatchAs(tutor, $"/lessons/{editedId}", new { topic = "Keep me" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var update = await app.Api.PatchAs(tutor, $"/lessons/series/{series.Id}", new
        {
            weekdays = "Tuesday",
            startTimeLocal = "18:00:00",
            durationMinutes = 90,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var result = (await update.Content.ReadFromJsonAsync<UpdateDto>())!;

        Assert.Equal("Tuesday", result.Series.Weekdays);
        Assert.Equal(new TimeOnly(18, 0), result.Series.StartTimeLocal);
        Assert.Equal(90, result.Series.DurationMinutes);
        // The Mondays the new schedule no longer places are reported as lost.
        Assert.Contains(result.RemovedLessons, l => l.Id == firstMondayId);
        Assert.DoesNotContain(result.RemovedLessons, l => l.Id == editedId);

        await using var db = app.CreateDbContext(tutorId);
        var rows = await db.Lessons
            .AsNoTracking()
            .Where(l => l.SeriesId == series.Id)
            .OrderBy(l => l.OccurrenceDate)
            .ToListAsync();

        // The hand-edited Monday stands exactly as it was, at its old length and wall clock.
        var kept = Assert.Single(rows, r => r.OccurrenceDate == editedDate);
        Assert.True(kept.IsCustomized);
        Assert.Equal("Keep me", kept.Topic);
        Assert.Equal(60, kept.DurationMinutes);

        // Everything else was written anew by the new rule: Tuesdays, 90 minutes, untouched.
        var regenerated = rows.Where(r => r.Id != kept.Id).ToList();
        Assert.NotEmpty(regenerated);
        Assert.All(regenerated, r => Assert.Equal(DayOfWeek.Tuesday, r.OccurrenceDate!.Value.DayOfWeek));
        Assert.All(regenerated, r => Assert.Equal(90, r.DurationMinutes));
        Assert.All(regenerated, r => Assert.False(r.IsCustomized));
        Assert.True(regenerated.Max(r => r.OccurrenceDate) <= DateOnly.FromDateTime(DateTime.UtcNow.Date).AddMonths(4).AddDays(1));

        // A time-only change re-places the very same dates, so the sweep deletes and the refill
        // re-inserts the same (SeriesId, OccurrenceDate) pairs: the two must not race that unique
        // index, and nothing is reported as lost because every date got its lesson back.
        var retime = await app.Api.PatchAs(
            tutor, $"/lessons/series/{series.Id}", new { startTimeLocal = "19:00:00" });
        Assert.Equal(HttpStatusCode.OK, retime.StatusCode);
        var retimed = (await retime.Content.ReadFromJsonAsync<UpdateDto>())!;

        Assert.Equal(new TimeOnly(19, 0), retimed.Series.StartTimeLocal);
        Assert.Empty(retimed.RemovedLessons);
        var afterRetime = await db.Lessons
            .AsNoTracking()
            .Where(l => l.SeriesId == series.Id)
            .ToListAsync();
        Assert.Equal(rows.Count, afterRetime.Count);
        Assert.Single(afterRetime, r => r.IsCustomized && r.OccurrenceDate == editedDate);
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

    /// <summary>The one create route without a repeat: the response carries the lesson half only.</summary>
    private async Task<LessonDto> CreateOneOff(string tutor, Guid studentId, DateOnly date, TimeOnly? time = null)
    {
        var resp = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date,
            startTimeLocal = (time ?? new TimeOnly(10, 0)).ToString("HH:mm:ss"),
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = (await resp.Content.ReadFromJsonAsync<CreateDto>())!;
        Assert.Null(created.Series);
        return created.Lesson!;
    }

    /// <summary>The same create route with a repeat: the response carries the series half only.</summary>
    private async Task<SeriesDto> CreateWeeklySeries(string tutor, Guid studentId, DateOnly startMonday)
    {
        var resp = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date = startMonday,
            startTimeLocal = "16:00:00",
            durationMinutes = 60,
            repeat = new { weekdays = "Monday" },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = (await resp.Content.ReadFromJsonAsync<CreateDto>())!;
        Assert.Null(created.Lesson);
        return created.Series!;
    }

    private async Task<List<LessonDto>> GetSchedule(string tutor, DateTimeOffset from, DateTimeOffset to)
    {
        var url = $"/lessons?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var resp = await app.Api.GetAs(tutor, url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<LessonDto>>())!;
    }

    /// <summary>The schedule over a window wide enough to hold <paramref name="date"/> in any zone.</summary>
    private Task<List<LessonDto>> GetScheduleAround(string tutor, DateOnly date) =>
        GetSchedule(tutor, DayUtc(date).AddDays(-1), DayUtc(date).AddDays(2));

    /// <summary>
    /// The id of the tutor's single lesson on <paramref name="date"/> — read the way a client reads
    /// it, off the schedule.
    /// </summary>
    private async Task<Guid> LessonIdOn(string tutor, DateOnly date) =>
        Assert.Single(await GetSchedule(tutor, DayUtc(date), DayUtc(date.AddDays(1)))).Id;

    private static DateOnly FutureDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(days);

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
        Guid Id, Guid StudentId, Guid? SeriesId, DateOnly? OccurrenceDate,
        DateTimeOffset StartUtc, DateTimeOffset EndUtc, int DurationMinutes, string Status,
        decimal Price, bool IsPaid, string? Topic, string? Description, DateTimeOffset CreatedAtUtc);

    private sealed record SeriesDto(
        Guid Id, Guid StudentId, string? Title, DateOnly StartDate, DateOnly? EndDate, string Weekdays,
        TimeOnly StartTimeLocal, int DurationMinutes, string TimeZoneId, decimal? Price, DateTimeOffset CreatedAtUtc);

    /// <summary>The one create route's answer: exactly one half is filled in.</summary>
    private sealed record CreateDto(LessonDto? Lesson, SeriesDto? Series);

    private sealed record CancelDto(SeriesDto Series, List<LessonDto> RemovedLessons);

    private sealed record UpdateDto(SeriesDto Series, List<LessonDto> RemovedLessons);

    private sealed record StudentDto(Guid Id, string Name, decimal Rate, string Status, DateTimeOffset CreatedAtUtc);
}
