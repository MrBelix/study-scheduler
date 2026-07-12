using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Thin adapter over <see cref="ITelegramBotClient"/>. Builds a one-button-per-row inline keyboard
/// (or none) and classifies transport failures by Telegram error code: a 403 is
/// <see cref="TelegramSendResult.Unreachable"/> (bot not started or blocked), a 400 is
/// <see cref="TelegramSendResult.PermanentFailure"/> (bad request), a 429 or 5xx is
/// <see cref="TelegramSendResult.TransientFailure"/>, and any lower-level request/HTTP failure is
/// transient. Not registered in DI here — that is a later stage.
/// </summary>
public sealed class TelegramBotSender(ITelegramBotClient bot, ILogger<TelegramBotSender> logger) : INotificationSender
{
    public async Task<TelegramSendResult> SendAsync(
        long chatId, string text, IReadOnlyList<NotificationButton> buttons, CancellationToken ct = default)
    {
        InlineKeyboardMarkup? markup = buttons.Count == 0
            ? null
            : new InlineKeyboardMarkup(buttons.Select(b =>
                new[] { InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackData) }));

        try
        {
            await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
            return TelegramSendResult.Delivered;
        }
        catch (ApiRequestException ex)
        {
            var result = ex.ErrorCode switch
            {
                403 => TelegramSendResult.Unreachable,       // bot not started or blocked by the user
                400 => TelegramSendResult.PermanentFailure,  // our bad request — won't succeed on retry
                _ => TelegramSendResult.TransientFailure,    // 429 / 5xx — retry next tick
            };
            logger.Log(
                result == TelegramSendResult.TransientFailure ? LogLevel.Error : LogLevel.Warning,
                ex,
                "Telegram API rejected message to chat {ChatId} with code {ErrorCode} ({Result})",
                chatId, ex.ErrorCode, result);
            return result;
        }
        catch (RequestException ex)
        {
            logger.LogError(ex, "Telegram request to chat {ChatId} failed; treating as transient", chatId);
            return TelegramSendResult.TransientFailure;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "HTTP failure sending to chat {ChatId}; treating as transient", chatId);
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
}
