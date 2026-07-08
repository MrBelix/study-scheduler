using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>HTTP handlers for the Notifications feature. Wired to routes in <see cref="NotificationsModule"/>.</summary>
internal static class Endpoints
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>
    /// Telegram webhook receiver. Anonymous by design — authenticity comes from the secret
    /// header Telegram echoes back, not from initData. Always answers 200 for authenticated
    /// requests so Telegram doesn't pile up retries; processing errors only get logged.
    /// </summary>
    public static async Task<Results<Ok, UnauthorizedHttpResult, NotFound>> Webhook(
        HttpRequest request,
        TelegramUpdate update,
        TelegramWebhookHandler handler,
        IOptions<NotificationsOptions> options,
        ILogger<TelegramWebhookHandler> logger,
        CancellationToken ct)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
            return TypedResults.NotFound(); // feature disabled — don't advertise the route

        if (request.Headers[SecretHeader] != secret)
            return TypedResults.Unauthorized();

        try
        {
            await handler.HandleAsync(update, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Telegram webhook update processing failed");
        }

        return TypedResults.Ok();
    }
}
