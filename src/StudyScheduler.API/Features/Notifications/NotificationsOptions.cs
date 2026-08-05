namespace StudyScheduler.API.Features.Notifications;

/// <summary>Configuration for the notification poller and runner. Bound from the "Notifications" section.</summary>
public sealed class NotificationsOptions
{
    /// <summary>
    /// How often the background poller wakes to plan, deliver and reconcile notifications. Kept at or
    /// below the minimum reminder lead time so a reminder is never skipped between two ticks.
    /// </summary>
    public int PollIntervalMinutes { get; init; } = 1;

    /// <summary>
    /// How long after a day's last lesson ends the evening summary waits before going out — enough
    /// slack for a lesson that ran over to still be marked from the app first.
    /// </summary>
    public int SummaryGraceMinutes { get; init; } = 15;

    /// <summary>
    /// Ceiling on Telegram API calls one tick may issue, so a large reconciliation backlog is drained
    /// across several ticks instead of tripping the platform's rate limits.
    /// </summary>
    public int MaxTelegramCallsPerTick { get; init; } = 300;

    /// <summary>
    /// Absolute HTTPS base URL of the Mini App, with no query string of its own (<c>?startapp=</c> is
    /// appended to it). Empty means no deep-link buttons are emitted at all — the dev/test default.
    /// </summary>
    public string? MiniAppUrl { get; init; }

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
