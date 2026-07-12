namespace StudyScheduler.API.Features.Notifications;

/// <summary>Configuration for the notification poller and runner. Bound from the "Notifications" section.</summary>
public sealed class NotificationsOptions
{
    /// <summary>
    /// How often the background poller wakes to plan and deliver notifications. Kept at or below the
    /// minimum reminder lead time so a reminder is never skipped between two ticks.
    /// </summary>
    public int PollIntervalMinutes { get; init; } = 1;

    /// <summary>
    /// How far back a just-ended lesson stays eligible for its follow-up prompt. Bounds the read
    /// range and lets a missed tick still catch a recently-finished lesson.
    /// </summary>
    public int FollowUpLookbackMinutes { get; init; } = 60;

    /// <summary>
    /// Public HTTPS URL Telegram should POST updates to. When empty the webhook is disabled: the
    /// registrar no-ops and the endpoint 404s. Requires <see cref="WebhookSecret"/> when set.
    /// </summary>
    public string? WebhookUrl { get; init; }

    /// <summary>
    /// Shared secret Telegram echoes back in the <c>X-Telegram-Bot-Api-Secret-Token</c> header and
    /// that the endpoint checks before processing an update. Empty disables the webhook endpoint.
    /// </summary>
    public string? WebhookSecret { get; init; }
}
