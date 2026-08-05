namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// One inline keyboard button. EXACTLY ONE of <paramref name="CallbackData"/> and
/// <paramref name="Url"/> is set — a Telegram button either posts a callback back to the bot or opens
/// a link, never both.
/// </summary>
public sealed record NotificationButton(string Text, string? CallbackData = null, string? Url = null);

/// <summary>
/// One row of the inline keyboard, exactly as the renderer laid it out. The sender never re-flows a
/// row: the layout is part of the rendered message the content hash covers.
/// </summary>
public sealed record NotificationButtonRow(IReadOnlyList<NotificationButton> Buttons);

/// <summary>
/// The outcome of a single Telegram call, classified for the caller's persistence decision.
/// <see cref="PermanentFailure"/> (a 400 bad request) still settles the dispatch — it will never
/// succeed on retry. <see cref="TransientFailure"/> (429/5xx/transport) is left unsettled so the next
/// tick retries it. <see cref="Unreachable"/> (403 — bot not started or blocked) also leaves it
/// unsettled but flips the tutor's reachability flag off so the poller stops targeting them until the
/// bot is re-enabled.
/// </summary>
public enum TelegramSendResult
{
    Delivered,
    TransientFailure,
    PermanentFailure,
    Unreachable,
}

/// <summary>
/// A send's classification plus the id of the message it produced — the handle every later edit of
/// that message needs. Null on anything but <see cref="TelegramSendResult.Delivered"/>.
/// </summary>
public sealed record TelegramSendOutcome(TelegramSendResult Result, int? MessageId);

/// <summary>
/// The bot output seam (messages + callback answers). The transport (Telegram.Bot) is an
/// implementation detail so the runner, the reconciler and the webhook handler can be tested without it.
/// </summary>
public interface INotificationSender
{
    /// <summary>Sends a new message with the given keyboard rows, laid out exactly as passed.</summary>
    Task<TelegramSendOutcome> SendAsync(
        long chatId, string text, IReadOnlyList<NotificationButtonRow> rows, CancellationToken ct = default);

    /// <summary>
    /// Replaces an existing message's text and keyboard (empty rows clear the keyboard, turning a
    /// tapped notification into a non-re-tappable record). Non-throwing: the outcome comes back
    /// classified so the caller can decide whether the row is still worth keeping live.
    /// </summary>
    Task<TelegramSendResult> EditMessageAsync(
        long chatId, int messageId, string text, IReadOnlyList<NotificationButtonRow> rows, CancellationToken ct = default);

    /// <summary>
    /// Answers a callback query so Telegram stops the button's progress spinner, optionally showing a
    /// short toast. A failure here is swallowed — it must not throw out of the webhook handler.
    /// </summary>
    Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken ct = default);
}
