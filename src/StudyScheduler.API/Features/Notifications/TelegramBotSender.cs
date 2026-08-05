using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Thin adapter over <see cref="ITelegramBotClient"/>. Emits the keyboard rows exactly as the
/// renderer laid them out (never re-flowed — the layout is part of the hashed message) and classifies
/// transport failures by Telegram error code: a 403 is <see cref="TelegramSendResult.Unreachable"/>
/// (bot not started or blocked), a 400 is <see cref="TelegramSendResult.PermanentFailure"/> (bad
/// request), a 429 or 5xx is <see cref="TelegramSendResult.TransientFailure"/>, and any lower-level
/// request/HTTP failure is transient. Messages go out as HTML with link previews off.
/// </summary>
public sealed class TelegramBotSender(ITelegramBotClient bot, ILogger<TelegramBotSender> logger) : INotificationSender
{
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    public async Task<TelegramSendOutcome> SendAsync(
        long chatId, string text, IReadOnlyList<NotificationButtonRow> rows, CancellationToken ct = default)
    {
        try
        {
            var message = await bot.SendMessage(
                chatId, text,
                parseMode: ParseMode.Html,
                replyMarkup: Markup(rows),
                linkPreviewOptions: NoPreview,
                cancellationToken: ct);
            return new TelegramSendOutcome(TelegramSendResult.Delivered, message.MessageId);
        }
        catch (ApiRequestException ex)
        {
            var result = Classify(ex);
            logger.Log(
                result == TelegramSendResult.TransientFailure ? LogLevel.Error : LogLevel.Warning,
                ex,
                "Telegram API rejected message to chat {ChatId} with code {ErrorCode} ({Result})",
                chatId, ex.ErrorCode, result);
            return new TelegramSendOutcome(result, null);
        }
        catch (RequestException ex)
        {
            logger.LogError(ex, "Telegram request to chat {ChatId} failed; treating as transient", chatId);
            return new TelegramSendOutcome(TelegramSendResult.TransientFailure, null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "HTTP failure sending to chat {ChatId}; treating as transient", chatId);
            return new TelegramSendOutcome(TelegramSendResult.TransientFailure, null);
        }
    }

    public async Task<TelegramSendResult> EditMessageAsync(
        long chatId, int messageId, string text, IReadOnlyList<NotificationButtonRow> rows,
        CancellationToken ct = default)
    {
        try
        {
            await bot.EditMessageText(
                chatId, messageId, text,
                parseMode: ParseMode.Html,
                replyMarkup: Markup(rows),
                linkPreviewOptions: NoPreview,
                cancellationToken: ct);
            return TelegramSendResult.Delivered;
        }
        catch (ApiRequestException ex)
        {
            var result = ClassifyEdit(ex);
            logger.Log(
                result == TelegramSendResult.Delivered ? LogLevel.Debug : LogLevel.Warning,
                ex,
                "Telegram API rejected edit of message {MessageId} in chat {ChatId} with code {ErrorCode} ({Result})",
                messageId, chatId, ex.ErrorCode, result);
            return result;
        }
        catch (RequestException ex)
        {
            logger.LogError(
                ex, "Telegram request editing message {MessageId} in chat {ChatId} failed; treating as transient",
                messageId, chatId);
            return TelegramSendResult.TransientFailure;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex, "HTTP failure editing message {MessageId} in chat {ChatId}; treating as transient",
                messageId, chatId);
            return TelegramSendResult.TransientFailure;
        }
    }

    public async Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken ct = default)
    {
        // Answering is best-effort: the mutation already happened, so a failed answer (e.g. an expired
        // query) must never bubble out of the handler. Classify only for the log, then swallow.
        try
        {
            await bot.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(
                ex, "Telegram API rejected callback answer {CallbackQueryId} with code {ErrorCode}",
                callbackQueryId, ex.ErrorCode);
        }
        catch (RequestException ex)
        {
            logger.LogWarning(ex, "Telegram request answering callback {CallbackQueryId} failed", callbackQueryId);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "HTTP failure answering callback {CallbackQueryId}", callbackQueryId);
        }
    }

    /// <summary>The keyboard exactly as the renderer laid it out; no rows at all clears it.</summary>
    private static InlineKeyboardMarkup? Markup(IReadOnlyList<NotificationButtonRow> rows) =>
        rows.Count == 0
            ? null
            : new InlineKeyboardMarkup(rows.Select(row => row.Buttons.Select(Button)));

    private static InlineKeyboardButton Button(NotificationButton button) =>
        button.Url is { } url
            ? InlineKeyboardButton.WithUrl(button.Text, url)
            : InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackData ?? string.Empty);

    private static TelegramSendResult Classify(ApiRequestException ex) => ex.ErrorCode switch
    {
        403 => TelegramSendResult.Unreachable,       // bot not started or blocked by the user
        400 => TelegramSendResult.PermanentFailure,  // our bad request — won't succeed on retry
        _ => TelegramSendResult.TransientFailure,    // 429 / 5xx — retry next tick
    };

    /// <summary>
    /// One 400 an edit answers with is not a failure at all: "message is not modified" means the
    /// screen already says exactly what we wanted it to say. The other named 400s ("message to edit
    /// not found", "MESSAGE_ID_INVALID") are a message that no longer exists and can never be edited
    /// again — permanent, which is what the send classification already calls every 400.
    /// </summary>
    private static TelegramSendResult ClassifyEdit(ApiRequestException ex) =>
        ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)
            ? TelegramSendResult.Delivered
            : Classify(ex);
}
