using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// End-to-end tests for the Lessons feature over the real stack (SQL Server container + API +
/// Telegram auth): tenant isolation, overlap protection (409), virtual recurrence (on-the-fly
/// expansion, materialize-on-demand mutations) and series lifecycle. Each test uses distinct
/// tutor ids so the shared database stays isolated.
/// </summary>
[Collection(nameof(AppCollection))]
public class LessonsTests(AppFixture app)
{
    // Anchor everything ~30 days in the future so slots are always "upcoming".
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

        // Cross-tenant occurrence mutations must 404 too.
        var foreignPatch = await app.Api.PatchAs(
            tutorB, OccurrenceUrl(series.Id, BaseDate.AddDays(1)), new { topic = "Hijack" });
        Assert.Equal(HttpStatusCode.NotFound, foreignPatch.StatusCode);

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
    public async Task Create_lesson_with_topic_and_description_returns_them()
    {
        var tutor = TelegramInitData.ForUser(3115, "Alice");
        var student = await CreateStudent(tutor);

        var response = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId = student.Id,
            startUtc = BaseUtc.AddHours(9),
            durationMinutes = 60,
            topic = "Derivatives",
            description = "Chain rule, product rule; homework review.",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var lesson = await ReadAs<LessonDto>(response);
        Assert.Equal("Derivatives", lesson.Topic);
        Assert.Equal("Chain rule, product rule; homework review.", lesson.Description);
        Assert.False(lesson.IsVirtual);

        // Length limits are enforced (Topic ≤ 200, Description ≤ 2000).
        var tooLong = await app.Api.PostAs(tutor, "/lessons", new
        {
            studentId = student.Id,
            startUtc = BaseUtc.AddHours(11),
            durationMinutes = 60,
            topic = new string('x', 201),
            description = new string('x', 2001),
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
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
        var noop = await app.Api.PatchAs(tutor, $"/lessons/{second.Id}", new
        {
            isPaid = true,
            topic = "Integrals",
            description = "Substitution method",
        });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        var updated = await ReadAs<LessonDto>(noop);
        Assert.True(updated.IsPaid);
        Assert.Equal("Integrals", updated.Topic);
        Assert.Equal("Substitution method", updated.Description);
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
    public async Task Get_lessons_expands_series_virtually_without_persisting_rows()
    {
        var tutor = TelegramInitData.ForUser(3109, "Alice");
        var student = await CreateStudent(tutor, rate: 250m);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0)); // open-ended

        // First month: 4 virtual slots with the student's rate and the series id — no db rows.
        var month = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(4, month.Count);
        Assert.All(month, l =>
        {
            Assert.True(l.IsVirtual);
            Assert.Null(l.Id);
            Assert.Equal(series.Id, l.SeriesId);
            Assert.NotNull(l.OccurrenceDate);
            Assert.Equal(250m, l.Price);
            Assert.Equal("Scheduled", l.Status);
        });

        // Same read again: identical result, still virtual (reads never write).
        var again = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(4, again.Count);
        Assert.All(again, l => Assert.True(l.IsVirtual));

        // A far-future window expands independently — open-ended series never run out.
        Assert.Equal(2, (await ListLessons(tutor, BaseUtc.AddDays(56), BaseUtc.AddDays(70))).Count);
    }

    [Fact]
    public async Task Patching_virtual_occurrence_materializes_it_on_demand()
    {
        var tutor = TelegramInitData.ForUser(3116, "Alice");
        var student = await CreateStudent(tutor, rate: 200m);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));

        var target = (await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28)))[1];
        Assert.True(target.IsVirtual);

        // First mutation materializes the slot and applies the patch in one step.
        var patch = await app.Api.PatchAs(tutor, OccurrenceUrl(series.Id, target.OccurrenceDate!.Value), new
        {
            topic = "Fractions",
            description = "Adding with unlike denominators",
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var materialized = await ReadAs<LessonDto>(patch);
        Assert.False(materialized.IsVirtual);
        Assert.NotNull(materialized.Id);
        Assert.Equal(series.Id, materialized.SeriesId);
        Assert.Equal(target.OccurrenceDate, materialized.OccurrenceDate);
        Assert.Equal(target.StartUtc, materialized.StartUtc);
        Assert.Equal(200m, materialized.Price); // price snapshot from the student's rate
        Assert.Equal("Fractions", materialized.Topic);
        Assert.Equal("Adding with unlike denominators", materialized.Description);

        // The merged list swaps exactly that slot for the physical row — no duplicates.
        var after = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(4, after.Count);
        var physical = Assert.Single(after, l => !l.IsVirtual);
        Assert.Equal(materialized.Id, physical.Id);
        Assert.Equal("Fractions", physical.Topic);

        // A second patch on the same date updates the same physical row (idempotent mapping).
        var second = await app.Api.PatchAs(tutor, OccurrenceUrl(series.Id, target.OccurrenceDate!.Value), new
        {
            status = "Cancelled",
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(materialized.Id, (await ReadAs<LessonDto>(second)).Id);

        // A date that is not a slot of the series (wrong weekday) → 404.
        var wrongDay = await app.Api.PatchAs(
            tutor, OccurrenceUrl(series.Id, target.OccurrenceDate!.Value.AddDays(1)), new { topic = "Nope" });
        Assert.Equal(HttpStatusCode.NotFound, wrongDay.StatusCode);
    }

    [Fact]
    public async Task Rescheduling_virtual_occurrence_does_not_conflict_with_its_own_slot()
    {
        var tutor = TelegramInitData.ForUser(3117, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));

        var target = (await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28)))[0];

        // Shift by 15 minutes — overlaps the slot's own virtual occurrence, which must be exempt.
        var reschedule = await app.Api.PatchAs(tutor, OccurrenceUrl(series.Id, target.OccurrenceDate!.Value), new
        {
            startUtc = target.StartUtc.AddMinutes(15),
        });
        Assert.Equal(HttpStatusCode.OK, reschedule.StatusCode);
        var moved = await ReadAs<LessonDto>(reschedule);
        Assert.Equal(target.StartUtc.AddMinutes(15), moved.StartUtc);
        Assert.Equal(target.OccurrenceDate, moved.OccurrenceDate); // original slot date is kept

        // The original slot is governed by the physical row now — no ghost virtual twin.
        var after = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(4, after.Count);
        Assert.Single(after, l => !l.IsVirtual);
    }

    [Fact]
    public async Task Oneoff_into_unmaterialized_series_slot_returns_409()
    {
        var tutor = TelegramInitData.ForUser(3110, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));

        // The slot two weeks out has no physical row, yet it must be protected.
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
    public async Task Ending_future_series_removes_untouched_slots_but_keeps_touched_lessons()
    {
        var tutor = TelegramInitData.ForUser(3112, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);
        var series = await CreateSeries(tutor, student.Id, BaseDate, new TimeOnly(16, 0));

        var slots = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        Assert.Equal(4, slots.Count);

        // One occurrence is already completed (materialized) — ending the series must keep it.
        var completed = slots[0];
        var complete = await app.Api.PatchAs(
            tutor, OccurrenceUrl(series.Id, completed.OccurrenceDate!.Value), new { status = "Completed" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        // The series starts 30 days from now, so ending it today ends it before it ever ran.
        var cancel = await app.Api.PostAs(tutor, $"/lessons/series/{series.Id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var ended = await ReadAs<SeriesDto>(cancel);
        Assert.False(ended.IsActive);

        // No mass-cancellation: the touched (completed) row survives untouched, the untouched
        // virtual slots simply stop expanding.
        var after = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28));
        var survivor = Assert.Single(after);
        Assert.False(survivor.IsVirtual);
        Assert.Equal("Completed", survivor.Status);

        // Nothing expands further out either.
        Assert.Empty(await ListLessons(tutor, BaseUtc.AddDays(28), BaseUtc.AddDays(56)));
    }

    [Fact]
    public async Task Ending_started_series_keeps_past_occurrences_and_stops_future_ones()
    {
        var tutor = TelegramInitData.ForUser(3118, "Alice");
        var student = await CreateStudent(tutor);
        await SetProfile(tutor);

        // Started ~12 days ago (BaseDate is +30d, so -42d ≈ 12 days in the past).
        var startDate = BaseDate.AddDays(-42);
        var series = await CreateSeries(tutor, student.Id, startDate, new TimeOnly(9, 0));

        var pastWindowFrom = BaseUtc.AddDays(-42);
        var pastBefore = await ListLessons(tutor, pastWindowFrom, pastWindowFrom.AddDays(13));
        Assert.Equal(2, pastBefore.Count);

        var cancel = await app.Api.PostAs(tutor, $"/lessons/series/{series.Id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var ended = await ReadAs<SeriesDto>(cancel);

        // Ended mid-life: stays active with a bounded end date, so history keeps expanding.
        Assert.True(ended.IsActive);
        Assert.NotNull(ended.EndDate);

        var pastAfter = await ListLessons(tutor, pastWindowFrom, pastWindowFrom.AddDays(13));
        Assert.Equal(2, pastAfter.Count);
        Assert.All(pastAfter, l => Assert.True(l.IsVirtual));

        // Future slots are gone.
        Assert.Empty(await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(28)));
    }

    [Fact]
    public async Task Shortening_series_end_date_clips_future_slots_without_cancelling_rows()
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

        // The tail is simply not expanded any more — nothing to cancel, nothing cancelled.
        var after = await ListLessons(tutor, BaseUtc, BaseUtc.AddDays(56));
        Assert.Equal(2, after.Count);
        Assert.All(after, l =>
        {
            Assert.True(l.IsVirtual);
            Assert.Equal("Scheduled", l.Status);
        });
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

    /// <summary>UTC start of a series occurrence, computed analytically (DST-aware).</summary>
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

    private static string OccurrenceUrl(Guid seriesId, DateOnly occurrenceDate) =>
        $"/lessons/series/{seriesId}/occurrences/{occurrenceDate:yyyy-MM-dd}";

    private static async Task<T> ReadAs<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>())!;

    private sealed record StudentDto(Guid Id, string Name, decimal Rate);

    private sealed record LessonDto(
        Guid? Id,
        Guid StudentId,
        Guid? SeriesId,
        DateOnly? OccurrenceDate,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Status,
        decimal Price,
        bool IsPaid,
        string? Topic,
        string? Description,
        bool IsVirtual);

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
}
