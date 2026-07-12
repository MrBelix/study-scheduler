using StudyScheduler.API.Core.Scheduling;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>The two bot notifications a lesson can trigger. Lives in the feature, not the domain.</summary>
public enum NotificationKind
{
    Reminder,
    FollowUp,
}

/// <summary>A notification the planner decided is due for a specific schedule entry this tick.</summary>
public sealed record DueNotification(NotificationKind Kind, ScheduleEntry Entry);

/// <summary>
/// The outcome of a single Telegram send, classified for the runner's persistence decision.
/// <see cref="PermanentFailure"/> (a 400 bad request) still marks the notification sent — it will
/// never succeed on retry. <see cref="TransientFailure"/> (429/5xx/transport) is left unmarked so
/// the next tick retries it. <see cref="Unreachable"/> (403 — bot not started or blocked) leaves
/// the notification unmarked but flips the tutor's reachability flag off so the poller stops
/// targeting them until the bot is re-enabled.
/// </summary>
public enum TelegramSendResult
{
    Delivered,
    TransientFailure,
    PermanentFailure,
    Unreachable,
}

/// <summary>An inline keyboard button: display text plus the callback payload the bot receives back.</summary>
public sealed record NotificationButton(string Text, string CallbackData);

/// <summary>
/// The bot output seam (messages + callback answers). The transport (Telegram.Bot) is an
/// implementation detail so the runner and the webhook handler can be tested without it.
/// </summary>
public interface INotificationSender
{
    Task<TelegramSendResult> SendAsync(
        long chatId, string text, IReadOnlyList<NotificationButton> buttons, CancellationToken ct = default);

    /// <summary>
    /// Answers a callback query so Telegram stops the button's progress spinner, optionally showing a
    /// short toast. A failure here is swallowed — it must not throw out of the webhook handler.
    /// </summary>
    Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken ct = default);
}
