namespace StudyScheduler.API.Features.Notifications;

/// <summary>Settings of the bot notification pipeline (bound from the "Notifications" section).</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>How often the poller scans for due notifications.</summary>
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// How far back a follow-up may reach. Bounds catch-up after downtime so a fresh deploy
    /// doesn't blast prompts for long-finished lessons.
    /// </summary>
    public int FollowUpLookbackMinutes { get; set; } = 30;

    /// <summary>
    /// Shared secret Telegram echoes in <c>X-Telegram-Bot-Api-Secret-Token</c>. The webhook
    /// endpoint (and inline buttons) are disabled while this is unset.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Public URL of the webhook endpoint (e.g. <c>https://…/telegram/webhook</c>). When set
    /// together with <see cref="WebhookSecret"/>, the poller registers it via <c>setWebhook</c>
    /// on startup; leave unset locally — Telegram can't reach localhost anyway.
    /// </summary>
    public string? WebhookUrl { get; set; }
}
