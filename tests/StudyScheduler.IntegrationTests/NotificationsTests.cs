using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// End-to-end tests for the Telegram webhook: secret enforcement and follow-up button presses
/// mutating lessons (including on-demand materialization of virtual series slots). Outbound Bot
/// API calls (answerCallbackQuery / editMessageText) fail against the test token by design and
/// are swallowed by the handler — the lesson mutation is what's asserted.
/// </summary>
[Collection(nameof(AppCollection))]
public class NotificationsTests(AppFixture app)
{
    // Must match the fallback secret in AppHost.cs.
    private const string WebhookSecret = "TEST-webhook-secret";

    private static readonly DateTimeOffset BaseUtc =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(30);

    private static readonly DateOnly BaseDate = DateOnly.FromDateTime(BaseUtc.UtcDateTime);

    [Fact]
    public async Task Webhook_rejects_requests_without_the_secret_header()
    {
        var update = CallbackUpdate(9999, "p:L:00000000000000000000000000000000");

        var missing = await app.Api.SendAsync(WebhookRequest(update, secret: null));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrong = await app.Api.SendAsync(WebhookRequest(update, secret: "not-the-secret"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Paid_button_marks_one_off_lesson_completed_and_paid()
    {
        var tutorId = 3301L;
        var tutor = TelegramInitData.ForUser(tutorId, "Alice");
        var student = await CreateStudent(tutor);
        var lesson = await CreateLesson(tutor, student.Id, BaseUtc.AddHours(9));

        var data = $"p:L:{lesson.Id:N}";
        var response = await app.Api.SendAsync(WebhookRequest(CallbackUpdate(tutorId, data), WebhookSecret));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await ReadAs<LessonDto>(await app.Api.GetAs(tutor, $"/lessons/{lesson.Id}"));
        Assert.Equal("Completed", updated.Status);
        Assert.True(updated.IsPaid);
    }

    [Fact]
    public async Task Cancel_button_materializes_a_virtual_occurrence_and_cancels_it()
    {
        var tutorId = 3302L;
        var tutor = TelegramInitData.ForUser(tutorId, "Alice");
        var student = await CreateStudent(tutor);
        Assert.Equal(
            HttpStatusCode.OK,
            (await app.Api.PutAs(tutor, "/profile", new { timeZoneId = "Europe/Kyiv" })).StatusCode);

        var occurrenceDate = BaseDate.AddDays(1);
        var seriesResponse = await app.Api.PostAs(tutor, "/lessons/series", new
        {
            studentId = student.Id,
            startDate = occurrenceDate,
            weekdays = occurrenceDate.DayOfWeek.ToString(),
            startTimeLocal = new TimeOnly(10, 0),
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Created, seriesResponse.StatusCode);
        var series = await ReadAs<SeriesDto>(seriesResponse);

        var data = $"c:S:{series.Id:N}:{occurrenceDate:yyyyMMdd}";
        var response = await app.Api.SendAsync(WebhookRequest(CallbackUpdate(tutorId, data), WebhookSecret));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The slot became a physical, cancelled lesson.
        var lessons = await ReadAs<List<LessonDto>>(await app.Api.GetAs(
            tutor, $"/lessons?from={BaseUtc:yyyy-MM-ddTHH:mm:ssZ}&to={BaseUtc.AddDays(7):yyyy-MM-ddTHH:mm:ssZ}"));
        var slot = Assert.Single(lessons, l => l.SeriesId == series.Id);
        Assert.Equal("Cancelled", slot.Status);
        Assert.False(slot.IsVirtual);
        Assert.NotNull(slot.Id);
    }

    [Fact]
    public async Task Foreign_tutor_callback_cannot_mutate_someone_elses_lesson()
    {
        var owner = TelegramInitData.ForUser(3303, "Alice");
        var student = await CreateStudent(owner);
        var lesson = await CreateLesson(owner, student.Id, BaseUtc.AddHours(11));

        // The callback arrives from a different Telegram user (e.g. a forwarded button).
        var data = $"c:L:{lesson.Id:N}";
        var response = await app.Api.SendAsync(WebhookRequest(CallbackUpdate(3304, data), WebhookSecret));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // acknowledged, but…

        var unchanged = await ReadAs<LessonDto>(await app.Api.GetAs(owner, $"/lessons/{lesson.Id}"));
        Assert.Equal("Scheduled", unchanged.Status); // …nothing was applied
    }

    private static HttpRequestMessage WebhookRequest(object update, string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook")
        {
            Content = JsonContent.Create(update),
        };
        if (secret is not null)
            request.Headers.TryAddWithoutValidation("X-Telegram-Bot-Api-Secret-Token", secret);
        return request;
    }

    private static object CallbackUpdate(long fromUserId, string data) => new
    {
        callback_query = new
        {
            id = "test-callback",
            from = new { id = fromUserId, first_name = "Alice" },
            message = new { message_id = 42, chat = new { id = fromUserId } },
            data,
        },
    };

    private async Task<StudentDto> CreateStudent(string initData)
    {
        var response = await app.Api.PostAs(initData, "/students", new { name = "Kid", rate = 100m });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAs<StudentDto>(response);
    }

    private async Task<LessonDto> CreateLesson(string initData, Guid studentId, DateTimeOffset startUtc)
    {
        var response = await app.Api.PostAs(initData, "/lessons", new
        {
            studentId,
            startUtc,
            durationMinutes = 60,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAs<LessonDto>(response);
    }

    private static async Task<T> ReadAs<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>())!;

    private sealed record StudentDto(Guid Id, string Name);

    private sealed record LessonDto(
        Guid? Id,
        Guid StudentId,
        Guid? SeriesId,
        DateOnly? OccurrenceDate,
        DateTimeOffset StartUtc,
        string Status,
        bool IsPaid,
        bool IsVirtual);

    private sealed record SeriesDto(Guid Id, Guid StudentId, string TimeZoneId);
}
