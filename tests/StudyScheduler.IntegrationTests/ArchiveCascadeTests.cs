using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// End-to-end coverage of the archive cascade over the real stack (PostgreSQL container + API):
/// archiving a student means the tutor stopped teaching them, so their running series is ended and
/// everything of theirs still ahead is deleted — completed history excepted — and no new lesson can
/// be booked for them afterwards.
/// </summary>
[Collection(nameof(AppCollection))]
public class ArchiveCascadeTests(AppFixture app)
{
    [Fact]
    public async Task Archiving_a_student_ends_their_series_and_clears_what_was_ahead()
    {
        const long tutorId = 5001;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();

        // A weekly series (rows written months ahead) plus a one-off on another day.
        var series = await CreateWeeklySeries(tutor, studentId, monday);
        var oneOff = await CreateOneOff(tutor, studentId, monday.AddDays(1));

        // Everything is on the calendar before the archive: the weekly rows and the one-off alike.
        var before = await GetSchedule(tutor, DateTime.UtcNow, DateTime.UtcNow.AddMonths(6), studentId);
        Assert.Contains(before, l => l.Id == oneOff.Id);
        Assert.True(before.Count > 2);

        // Act
        var archive = await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        var archived = (await archive.Content.ReadFromJsonAsync<StudentDto>())!;
        Assert.Equal("Archived", archived.Status);
        // The response is the plain student shape it has always been — the cascade is a side effect.
        Assert.Null(archived.NextLesson);

        // Nothing of theirs is left ahead, generated rows and hand-placed ones alike.
        Assert.Empty(await GetSchedule(tutor, DateTime.UtcNow, DateTime.UtcNow.AddMonths(6), studentId));

        // The rule itself is stopped: its last possible lesson day is yesterday in the tutor's zone,
        // so the nightly generator can never refill the window.
        var read = await app.Api.GetAs(tutor, $"/lessons/series/{series.Id}");
        var stopped = (await read.Content.ReadFromJsonAsync<SeriesDto>())!;
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        Assert.NotNull(stopped.EndDate);
        Assert.InRange(stopped.EndDate!.Value, utcToday.AddDays(-2), utcToday);

        // And the details screen agrees: nothing upcoming, no series still running.
        var details = await GetDetails(tutor, studentId);
        Assert.Null(details.NextLesson);
        Assert.Empty(details.Series);

        // The rows themselves, read past the API: the future is empty for this student.
        await using var db = app.CreateDbContext(tutorId);
        Assert.Empty(await db.Lessons
            .AsNoTracking()
            .Where(l => l.StudentId == studentId && l.StartUtc > DateTimeOffset.UtcNow)
            .ToListAsync());
    }

    [Fact]
    public async Task Archiving_a_student_keeps_the_lesson_already_recorded_as_completed()
    {
        const long tutorId = 5006;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        await CreateWeeklySeries(tutor, studentId, monday);

        // One occurrence the tutor already recorded as done and paid — the bot's buttons do exactly
        // this, and it can happen before the scheduled end.
        var completedId = await LessonIdOn(tutor, monday.AddDays(7));
        var settle = await app.Api.PatchAs(
            tutor, $"/lessons/{completedId}", new { status = "Completed", isPaid = true });
        Assert.Equal(HttpStatusCode.OK, settle.StatusCode);

        // Act
        var archive = await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        // Assert
        // A completed lesson carries the payment the debt dashboard counts: it is history, and the
        // cascade sweeps plans only.
        await using var db = app.CreateDbContext(tutorId);
        var left = await db.Lessons
            .AsNoTracking()
            .Where(l => l.StudentId == studentId && l.StartUtc > DateTimeOffset.UtcNow)
            .ToListAsync();
        var kept = Assert.Single(left);
        Assert.Equal(completedId, kept.Id);
        Assert.True(kept.IsPaid);
    }

    [Fact]
    public async Task Restoring_a_student_does_not_bring_the_schedule_back()
    {
        const long tutorId = 5002;
        var tutor = TelegramInitData.ForUser(tutorId, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        await CreateWeeklySeries(tutor, studentId, NextMonday());

        await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Archived" });

        // Act — the tutor takes the student back on.
        var restore = await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Active" });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("Active", (await restore.Content.ReadFromJsonAsync<StudentDto>())!.Status);

        // Assert — a pure status flip: the swept lessons are gone for good and the series stays ended.
        var details = await GetDetails(tutor, studentId);
        Assert.Null(details.NextLesson);
        Assert.Empty(details.Series);

        await using var db = app.CreateDbContext(tutorId);
        Assert.Empty(await db.Lessons
            .AsNoTracking()
            .Where(l => l.StudentId == studentId && l.StartUtc > DateTimeOffset.UtcNow)
            .ToListAsync());
    }

    [Fact]
    public async Task Booking_for_an_archived_student_returns_bad_request()
    {
        var tutor = TelegramInitData.ForUser(5003, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Archived" });

        // Act — the one create form, both of its branches.
        var oneOff = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date = FutureDate(days: 3),
            startTimeLocal = "10:00:00",
            durationMinutes = 60,
        });
        var repeating = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date = NextMonday(),
            startTimeLocal = "16:00:00",
            durationMinutes = 60,
            repeat = new { weekdays = "Monday" },
        });

        // Assert — refused on the student, not on the time: the tutor stopped teaching them.
        Assert.Equal(HttpStatusCode.BadRequest, oneOff.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, repeating.StatusCode);
        Assert.Contains(await ProblemFields(oneOff), f => f.Equals("StudentId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(await ProblemFields(repeating), f => f.Equals("StudentId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reopening_the_series_of_an_archived_student_returns_bad_request()
    {
        var tutor = TelegramInitData.ForUser(5007, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var series = await CreateWeeklySeries(tutor, studentId, NextMonday());
        await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Archived" });

        // Act — clearing the end date the cascade set would put the rule back in business, and the
        // nightly generator would keep extending it every night after.
        var reopen = await app.Api.PatchAs(
            tutor, $"/lessons/series/{series.Id}", new { clearEndDate = true });

        // Assert — refused on the student, like booking: the rule stays stopped and the schedule
        // stays empty, so the invariant survives the one edit that could undo it.
        Assert.Equal(HttpStatusCode.BadRequest, reopen.StatusCode);
        Assert.Contains(await ProblemFields(reopen), f => f.Equals("StudentId", StringComparison.OrdinalIgnoreCase));

        var read = await app.Api.GetAs(tutor, $"/lessons/series/{series.Id}");
        Assert.NotNull((await read.Content.ReadFromJsonAsync<SeriesDto>())!.EndDate);
        Assert.Empty(await GetSchedule(tutor, DateTime.UtcNow, DateTime.UtcNow.AddMonths(6), studentId));
    }

    [Fact]
    public async Task Unsettling_the_kept_completed_lesson_of_an_archived_student_returns_bad_request()
    {
        var tutor = TelegramInitData.ForUser(5008, "Al");
        await SetProfile(tutor);
        var studentId = await CreateStudent(tutor);
        var monday = NextMonday();
        await CreateWeeklySeries(tutor, studentId, monday);

        // The one row that survives the cascade ahead of them: recorded as done before its time.
        var completedId = await LessonIdOn(tutor, monday.AddDays(7));
        await app.Api.PatchAs(tutor, $"/lessons/{completedId}", new { status = "Completed" });
        await app.Api.PatchAs(tutor, $"/students/{studentId}", new { status = "Archived" });

        // Act — the correction back to Scheduled, which any other completed lesson still accepts.
        var reopen = await app.Api.PatchAs(
            tutor, $"/lessons/{completedId}", new { status = "Scheduled" });

        // Assert — it would hand the archived student a lesson to be taught again, so it is refused
        // and the row stays the history it became.
        Assert.Equal(HttpStatusCode.BadRequest, reopen.StatusCode);
        Assert.Contains(await ProblemFields(reopen), f => f.Equals("Status", StringComparison.OrdinalIgnoreCase));

        var read = await app.Api.GetAs(tutor, $"/lessons/{completedId}");
        Assert.Equal("Completed", (await read.Content.ReadFromJsonAsync<LessonDto>())!.Status);
    }

    [Fact]
    public async Task Archiving_leaves_another_tutors_schedule_untouched()
    {
        const long mineId = 5004;
        const long theirsId = 5005;
        var mine = TelegramInitData.ForUser(mineId, "Al");
        var theirs = TelegramInitData.ForUser(theirsId, "Bo");
        await SetProfile(mine);
        await SetProfile(theirs);
        var monday = NextMonday();

        var myStudent = await CreateStudent(mine);
        await CreateWeeklySeries(mine, myStudent, monday);
        var theirStudent = await CreateStudent(theirs);
        var theirSeries = await CreateWeeklySeries(theirs, theirStudent, monday);

        // Act
        var archive = await app.Api.PatchAs(mine, $"/students/{myStudent}", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        // Assert — the cascade reaches exactly one tenant's rows: the other tutor's schedule is whole.
        var read = await app.Api.GetAs(theirs, $"/lessons/series/{theirSeries.Id}");
        Assert.Null((await read.Content.ReadFromJsonAsync<SeriesDto>())!.EndDate);

        await using var db = app.CreateDbContext(theirsId);
        Assert.NotEmpty(await db.Lessons
            .AsNoTracking()
            .Where(l => l.StudentId == theirStudent && l.StartUtc > DateTimeOffset.UtcNow)
            .ToListAsync());
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

    private async Task<LessonDto> CreateOneOff(string tutor, Guid studentId, DateOnly date)
    {
        var resp = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId,
            date,
            startTimeLocal = "10:00:00",
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<CreateDto>())!.Lesson!;
    }

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
        return (await resp.Content.ReadFromJsonAsync<CreateDto>())!.Series!;
    }

    private async Task<List<LessonDto>> GetSchedule(
        string tutor, DateTime fromUtc, DateTime toUtc, Guid? studentId = null)
    {
        var from = new DateTimeOffset(fromUtc, TimeSpan.Zero);
        var to = new DateTimeOffset(toUtc, TimeSpan.Zero);
        var url = $"/lessons?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}"
            + (studentId is { } id ? $"&studentId={id}" : string.Empty);
        var resp = await app.Api.GetAs(tutor, url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<LessonDto>>())!;
    }

    private async Task<StudentDetailsDto> GetDetails(string tutor, Guid studentId)
    {
        var resp = await app.Api.GetAs(tutor, $"/students/{studentId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<StudentDetailsDto>())!;
    }

    /// <summary>The form fields a ValidationProblem payload blames — its <c>errors</c> keys.</summary>
    private static async Task<IEnumerable<string>> ProblemFields(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Errors.Keys;

    /// <summary>The id of the tutor's single lesson on <paramref name="date"/>, read off the schedule.</summary>
    private async Task<Guid> LessonIdOn(string tutor, DateOnly date) =>
        Assert.Single(await GetSchedule(
            tutor,
            date.ToDateTime(TimeOnly.MinValue),
            date.AddDays(1).ToDateTime(TimeOnly.MinValue))).Id;

    private static DateOnly FutureDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(days);

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
        DateTimeOffset StartUtc, string Status, bool IsPaid);

    private sealed record SeriesDto(Guid Id, Guid StudentId, DateOnly StartDate, DateOnly? EndDate);

    private sealed record CreateDto(LessonDto? Lesson, SeriesDto? Series);

    private sealed record NextLessonDto(DateTimeOffset StartUtc, string? Subject);

    private sealed record StudentDto(Guid Id, string Name, string Status, NextLessonDto? NextLesson);

    private sealed record StudentDetailsDto(
        Guid Id, string Status, NextLessonDto? NextLesson, List<SeriesDto> Series);

    private sealed record ProblemDto(Dictionary<string, string[]> Errors);
}
