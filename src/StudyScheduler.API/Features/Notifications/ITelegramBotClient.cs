namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// The Telegram Bot API surface the notification pipeline consumes. Extracted so
/// <see cref="LessonNotifier"/>, <see cref="TelegramWebhookHandler"/> and the webhook
/// registration can be unit-tested with fakes instead of a live HTTP client.
/// All methods report failure as <c>false</c>, never throw (see <see cref="TelegramBotClient"/>).
/// </summary>
public interface ITelegramBotClient
{
    Task<bool> SendMessageAsync(
        long chatId,
        string text,
        IReadOnlyList<BotButton>? buttons = null,
        CancellationToken ct = default);

    Task<bool> AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken ct = default);

    /// <summary>Replaces the message text and drops its inline keyboard.</summary>
    Task<bool> EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken ct = default);

    Task<bool> SetWebhookAsync(string url, string secretToken, CancellationToken ct = default);
}
