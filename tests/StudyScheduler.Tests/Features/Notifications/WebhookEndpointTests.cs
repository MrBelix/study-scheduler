using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Tests.Core.Tenancy;
using StudyScheduler.Tests.Features.Lessons;
using StudyScheduler.Tests.Features.Reports;
using Xunit;
using WebhookEndpoints = StudyScheduler.API.Features.Notifications.Endpoints;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>
/// The webhook's front door. The secret header is the whole authorization of an anonymous endpoint
/// that then acts as the tutor its payload names, so these tests pin both halves: only the exact
/// secret gets in (compared without leaking where a wrong one diverges), and everything else is a
/// 404 that never reaches the handler — and therefore never establishes a tenant.
/// </summary>
public class WebhookEndpointTests
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const string Secret = "s3cr3t-token-value";
    private const long Tutor = 555;

    private readonly RecordingTutorScope _tenant = new();
    private readonly TelegramWebhookHandler _handler;

    public WebhookEndpointTests()
    {
        var uow = new FakeUnitOfWork();
        var lessons = new FakeLessonRepository(_tenant);
        var students = new FakeStudentRepository(_tenant);
        var series = new FakeLessonSeriesRepository(_tenant);
        var service = LessonServiceFactory.Create(_tenant, lessons, series, students, uow, TimeProvider.System);
        var renderer = new NotificationRenderer(
            Microsoft.Extensions.Options.Options.Create(new NotificationsOptions()));
        var views = new NotificationViewBuilder(
            lessons, students, series, new FakeStudentDebtReader(lessons), TimeProvider.System);
        _handler = new TelegramWebhookHandler(
            service, new FakeTutorProfileRepository(_tenant), new FakeNotificationDispatchRepository(_tenant),
            views, renderer, new FakeNotificationSender(), uow, _tenant, TimeProvider.System,
            NullLogger<TelegramWebhookHandler>.Instance);
    }

    /// <summary>A plain message update from <see cref="Tutor"/> — enough to see the handler run.</summary>
    private const string MessageUpdate = """
        {"update_id":1,"message":{"message_id":1,"date":1750000000,
        "chat":{"id":555,"type":"private"},"from":{"id":555,"is_bot":false,"first_name":"T"}}}
        """;

    [Fact]
    public async Task Webhook_ExactSecret_HandlesTheUpdate()
    {
        // Arrange
        var http = Request(MessageUpdate, presentedSecret: Secret);

        // Act
        var result = await WebhookEndpoints.Webhook(http, Options(Secret), _handler, CancellationToken.None);

        // Assert
        // The constant-time comparison still ACCEPTS the right secret: the body reached the handler,
        // which made the update's sender the scope's tenant.
        Assert.IsType<Ok>(result);
        Assert.Equal([Tutor], _tenant.Tenants);
    }

    [Theory]
    // Same length, one character off — the case an ordinary != would answer faster the earlier the
    // divergence, handing an attacker a byte-by-byte oracle on the secret.
    [InlineData("s3cr3t-token-valuX")]
    // A prefix and an extension of the real secret: comparing bytes must be length-safe, not throw
    // and not match on the common part.
    [InlineData("s3cr3t-token-valu")]
    [InlineData("s3cr3t-token-value-and-more")]
    [InlineData("")]
    public async Task Webhook_WrongSecret_ReturnsNotFoundWithoutHandling(string presentedSecret)
    {
        // Arrange
        var http = Request(MessageUpdate, presentedSecret);

        // Act
        var result = await WebhookEndpoints.Webhook(http, Options(Secret), _handler, CancellationToken.None);

        // Assert
        // 404, not 401: the endpoint's existence is not leaked either. And nothing past the gate ran,
        // so the payload never got to name a tenant.
        Assert.IsType<NotFound>(result);
        Assert.Empty(_tenant.Tenants);
    }

    [Fact]
    public async Task Webhook_MissingSecretHeader_ReturnsNotFoundWithoutHandling()
    {
        // Arrange
        var http = Request(MessageUpdate, presentedSecret: null);

        // Act
        var result = await WebhookEndpoints.Webhook(http, Options(Secret), _handler, CancellationToken.None);

        // Assert
        Assert.IsType<NotFound>(result);
        Assert.Empty(_tenant.Tenants);
    }

    [Fact]
    public async Task Webhook_SecretNotConfigured_ReturnsNotFoundWithoutHandling()
    {
        // Arrange
        // The webhook is disabled — no secret means no gate, so no update may be trusted at all.
        var http = Request(MessageUpdate, presentedSecret: Secret);

        // Act
        var result = await WebhookEndpoints.Webhook(http, Options(null), _handler, CancellationToken.None);

        // Assert
        Assert.IsType<NotFound>(result);
        Assert.Empty(_tenant.Tenants);
    }

    private static IOptions<NotificationsOptions> Options(string? secret) =>
        Microsoft.Extensions.Options.Options.Create(new NotificationsOptions { WebhookSecret = secret });

    private static DefaultHttpContext Request(string body, string? presentedSecret)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (presentedSecret is not null)
            http.Request.Headers[SecretHeader] = presentedSecret;
        return http;
    }
}
