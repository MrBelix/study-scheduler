using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// End-to-end tests for the Lessons feature over the real stack (SQL Server container + API +
/// Telegram auth): tenant isolation, overlap protection (409), lazy series materialization and
/// series lifecycle. Each test uses distinct tutor ids so the shared database stays isolated.
/// </summary>
[Collection(nameof(AppCollection))]
public class LessonsTests(AppFixture app)
{
    // Anchor everything ~30 days in the future so "cancel future lessons" semantics apply.
    private static readonly DateTimeOffset BaseUtc =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(30);

    private static readonly DateOnly BaseDate = DateOnly.FromDateTime(BaseUtc.UtcDateTime);

    [Fact]
    public async Task Tutor_cannot_access_another_tutors_lessons_or_series()
    {
        var tutorA = TelegramInitData.ForUser(3101, "Alice");
        var tutorB = TelegramInitData.ForUser(3102, "Bob");

        var student = await CreateStudent(tutorA);
        var lesson = await CreateLesson(tutorA, student.Id, BaseUtc.AddHours(10));

        await SetProfile(tutorA);
        var series = await CreateSeries(tutorA, student.Id, BaseDate.AddDays(1), new TimeOnly(9, 0));

        // Cross-tenant reads must 404 (not 403, so existence isn't leaked).
        Assert.Equal(HttpStatusCode.NotFound, (await app.Api.GetAs(tutorB, $"/lessons/{lesson.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Api.GetAs(tutorB, $"/lessons/series/{series.Id}")).StatusCode);

        // Lists are scoped.
        var bLessons = await ReadAs<List<LessonDto>>(await app.Api.GetAs(tutorB, LessonsUrl(BaseUtc, BaseUtc.AddDays(7))));
        Assert.Empty(bLessons);
        var bSeries = await ReadAs<List<SeriesDto>>(await app.Api.GetAs(tutorB, "/lessons/series"));
        Assert.Empty(bSeries);

        Assert.Equal(HttpStatusCode.OK, (await app.Api.GetAs(tutorA, $"/lessons/{lesson.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Api.GetAs(tutorA, $"/lessons/series/{series.Id}")).StatusCode);
    }

    [Fact]
    public async Task Create_lesson_without_price_defaults_to_student_rate()
    {
        var tutor = TelegramInitData.ForUser(3103, "Alice");
        var student = await CreateStudent(tutor, rate: 300m);

        var defaulted = await CreateLesson(tutor, student.Id, BaseUtc.AddHours(8));
        Assert.Equal(300m, defaulted.Price);

        var overridden = await CreateLesson(tutor, student.Id, BaseUtc.AddHours(12), price: 150m);
        Assert.Equal(150m, overridden.Price);
    }

    [Fact]
    public async Task Create_lesson_with_foreign_or_unknown_student_returns_validation_problem()
    {
        var tutorA = TelegramInitData.ForUser(3104, "Alice");
        var tutorB = TelegramInitData.ForUser(3105, "Bob");
        var foreign = await CreateStudent(tutorB);

        foreach (var studentId in new[] { foreign.Id, Guid.NewGuid() })
        {
            var response = await app.Api.PostAs(tutorA, "/lessons", new
            {
                studentId,
                startUtc = BaseUtc.AddHours(10),
                durationMinutes = 60,
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Create_overlapping_lesson_returns_409_with_conflicts()
    {
        var tutor = TelegramInitData.ForUser(3106, "Alice");
        var student = await CreateStudent(tutor);
        var start = BaseUtc.AddHours(10);

        var existing = await CreateLesson(tutor, student.Id, start);

        // Overlap → 409 naming the conflicting lesson.
        var overlap = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId = student.Id,
            startUtc = start.AddMinutes(30),
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        var conflicts = await ReadAs<ConflictResponseDto>(overlap);
        Assert.Contains(conflicts.Conflicts, c => c.LessonId == existing.Id);

        // Back-to-back (end == start) does not conflict.
        await CreateLesson(tutor, student.Id, start.AddMinutes(60));

        // A cancelled lesson frees its slot.
        var cancel = await app.Api.PatchAs(tutor, $"/lessons/{existing.Id}", new { status = "Cancelled" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await CreateLesson(tutor, student.Id, start);
    }

    [Fact]
    public async Task Reschedule_into_overlap_returns_409_but_noop_patch_succeeds()
    {
        var tutor = TelegramInitData.ForUser(3107, "Alice");
        var student = await CreateStudent(tutor);

        var first = await CreateLesson(tutor, student.Id, BaseUtc.AddHours(10));
        var second = await CreateLesson(tutor, student.Id, BaseUtc.AddHours(12));

        var intoOverlap = await app.Api.PatchAs(tutor, $"/lessons/{second.Id}", new
        {
            startUtc = first.StartUtc.AddMinutes(30),
        });
        Assert.Equal(HttpStatusCode.Conflict, intoOverlap.StatusCode);

        // Patching without moving the lesson never re-checks overlaps (self is not a conflict).
        var noop = await app.Api.PatchAs(tutor, $"/lessons/{second.Id}", new { isPaid = true, topic = "Integrals" });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        var updated = await ReadAs<LessonDto>(noop);
        Assert.True(updated.IsPaid);
    }

    [Fact]
    public async Task Create_series_requires_profile_time_zone()
    {
        var tutor = TelegramInitData.ForUser(3108, "Alice");
        var student = await CreateStudent(tutor);

        var withoutProfile = await app.Api.PostAs(tutor, "/lessons/series", new
        {
            studentId = student.Id,
            startDate = BaseDate,
            weekdays = BaseDate.DayOfWeek.ToString(),
            startTimeLocal = new TimeOnly(16, 0),
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.BadRequest, withoutProfile.StatusCode);

        await SetProfile(tutor);
        await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));
    }

    [Fact]
    public async Task Get_lessons_materializes_series_occurrences_idempotently()
    {
        var tutor = TelegramInitData.ForUser(3109, "Alice");
        var student = await CreateStudent(tutor, rate: 250m);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0)); // open-ended

        // First month: 4 occurrences materialize with the student's rate and the series id.
        var month = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(4, month.Count);
        Assert.All(month, l =>
        {
            Assert.Equal(series.Id, l.SeriesId);
            Assert.Equal(250m, l.Price);
            Assert.Equal("Scheduled", l.Status);
        });

        // Same read again: no duplicates.
        Assert.Equal(4, (await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28))).Count);

        // A far-future window materializes independently...
        Assert.Equal(2, (await ListLessons(tutor, BaseUtc.AddDays(56), BaseUtc.AddDays(70))).Count);

        // ...and re-reading the first window still yields exactly 4.
        Assert.Equal(4, (await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28))).Count);
    }

    [Fact]
    public async Task Oneoff_into_unmaterialized_series_slot_returns_409()
    {
        var tutor = TelegramInitData.ForUser(3110, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));

        // The slot two weeks out is not materialized (no GET happened), yet it must be protected.
        var occurrenceStartUtc = (await OccurrenceUtc(tutor, series.Id, BaseDate.AddDays(14)));
        var response = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId = student.Id,
            startUtc = occurrenceStartUtc.AddMinutes(30),
            durationMinutes = 60,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var conflicts = await ReadAs<ConflictResponseDto>(response);
        Assert.Contains(conflicts.Conflicts, c => c.SeriesId == series.Id);
    }

    [Fact]
    public async Task Series_over_existing_lesson_or_series_returns_409()
    {
        var tutor = TelegramInitData.ForUser(3111, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);

        // A lesson sitting exactly on the future weekly slot (16:00 Europe/Kyiv, DST-aware).
        await CreateLesson(tutor, student.Id, LocalToUtc(BaseDate.AddDays(7), new TimeOnly(16, 0)));

        var overLesson = await app.Api.PostAs(tutor, "/lessons/series", new
        {
            studentId = student.Id,
            startDate = BaseDate,
            weekdays = BaseDate.DayOfWeek.ToString(),
            startTimeLocal = new TimeOnly(16, 0),
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Conflict, overLesson.StatusCode);

        // A non-colliding series succeeds; a second series on the same weekly slot conflicts.
        await CreateSeries(tutor, student.Id, BaseDate.AddDays(1), new TimeOnly(16, 0));
        var overSeries = await app.Api.PostAs(tutor, "/lessons/series", new
        {
            studentId = student.Id,
            startDate = BaseDate.AddDays(8), // same weekday, one week later — open-ended overlap
            weekdays = BaseDate.AddDays(8).DayOfWeek.ToString(),
            startTimeLocal = new TimeOnly(16, 30),
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Conflict, overSeries.StatusCode);
    }

    [Fact]
    public async Task Cancel_series_cancels_only_future_scheduled_lessons()
    {
        var tutor = TelegramInitData.ForUser(3112, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));

        var lessons = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(4, lessons.Count);

        // One occurrence is already completed — cancelling the series must not touch it.
        var completed = lessons[0];
        await app.Api.PatchAs(tutor, $"/lessons/{completed.Id}", new { status = "Completed" });

        var cancel = await app.Api.PostAs(tutor, $"/lessons/series/{series.Id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(3, (await ReadAs<CancelDto>(cancel)).CancelledCount);

        var after = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal("Completed", after.Single(l => l.Id == completed.Id).Status);
        Assert.Equal(3, after.Count(l => l.Status == "Cancelled"));

        // Deactivated series materializes nothing new.
        Assert.Empty(await ListLessons(tutor, BaseUtc.AddDays(28), BaseUtc.AddDays(56)));
    }

    [Fact]
    public async Task Shortening_series_end_date_cancels_the_tail()
    {
        var tutor = TelegramInitData.ForUser(3113, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));

        Assert.Equal(4, (await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28))).Count);

        var shorten = await app.Api.PatchAs(tutor, $"/lessons/series/{series.Id}", new
        {
            endDate = BaseDate.AddDays(7),
        });
        Assert.Equal(HttpStatusCode.OK, shorten.StatusCode);

        var after = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(2, after.Count(l => l.Status == "Scheduled"));
        Assert.Equal(2, after.Count(l => l.Status == "Cancelled"));

        // The clipped range materializes nothing beyond the new end date.
        Assert.Equal(4, (await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(56))).Count);
    }

    [Fact]
    public async Task Get_lessons_requires_valid_range()
    {
        var tutor = TelegramInitData.ForUser(3114, "Alice");

        var inverted = await app.Api.GetAs(tutor, LessonsUrl(BaseUtc.AddDays(7), BaseUtc));
        Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);

        var tooWide = await app.Api.GetAs(tutor, LessonsUrl(BaseUtc, BaseUtc.AddDays(400)));
        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
    }

    // --- helpers ---

    private async Task<StudentDto> CreateStudent(string initData, decimal rate = 100m)
    {
        var response = await app.Api.PostAs(initData, "/students", new { name = "Kid", rate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAs<StudentDto>(response);
    }

    private async Task SetProfile(string initData, string timeZoneId = "Europe/Kyiv")
    {
        var response = await app.Api.PutAs(initData, "/profile", new { timeZoneId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<LessonDto> CreateLesson(
        string initData, Guid studentId, DateTimeOffset startUtc, int durationMinutes = 60, decimal? price = null)
    {
        var response = await app.Api.PostAs(initData, "/lessons", new
        {
            studentId,
            startUtc,
            durationMinutes,
            price,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAs<LessonDto>(response);
    }

    private async Task<SeriesDto> CreateSeries(
        string initData, Guid studentId, DateOnly startDate, TimeOnly startTimeLocal, DateOnly? endDate = null)
    {
        var response = await app.Api.PostAs(initData, "/lessons/series", new
        {
            studentId,
            startDate,
            weekdays = startDate.DayOfWeek.ToString(),
            startTimeLocal,
            durationMinutes = 60,
            endDate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAs<SeriesDto>(response);
    }

    private async Task<List<LessonDto>> ListLessons(string initData, DateTimeOffset from, DateTimeOffset to)
    {
        var response = await app.Api.GetAs(initData, LessonsUrl(from, to));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAs<List<LessonDto>>(response);
    }

    /// <summary>UTC start of a series occurrence, without materializing it (DST-aware).</summary>
    private async Task<DateTimeOffset> OccurrenceUtc(string initData, Guid seriesId, DateOnly occurrenceDate)
    {
        var response = await app.Api.GetAs(initData, $"/lessons/series/{seriesId}");
        var series = await ReadAs<SeriesDto>(response);
        return LocalToUtc(occurrenceDate, series.StartTimeLocal, series.TimeZoneId);
    }

    private static DateTimeOffset LocalToUtc(DateOnly date, TimeOnly time, string timeZoneId = "Europe/Kyiv")
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string LessonsUrl(DateTimeOffset from, DateTimeOffset to) =>
        $"/lessons?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static async Task<T> ReadAs<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>())!;

    private sealed record StudentDto(Guid Id, string Name, decimal Rate);

    private sealed record LessonDto(
        Guid Id,
        Guid StudentId,
        Guid? SeriesId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Status,
        decimal Price,
        bool IsPaid);

    private sealed record SeriesDto(
        Guid Id,
        Guid StudentId,
        string? Title,
        DateOnly StartDate,
        DateOnly? EndDate,
        TimeOnly StartTimeLocal,
        string TimeZoneId,
        bool IsActive);

    private sealed record ConflictDto(Guid? LessonId, Guid? SeriesId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    private sealed record ConflictResponseDto(string Message, List<ConflictDto> Conflicts);

    private sealed record CancelDto(int CancelledCount);
}
