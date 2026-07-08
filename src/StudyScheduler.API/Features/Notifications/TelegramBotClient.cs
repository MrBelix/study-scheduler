using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Authentication;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>One inline keyboard button: a label and the callback data the tap sends back.</summary>
public sealed record BotButton(string Text, string CallbackData);

/// <summary>
/// Thin Telegram Bot API client (sendMessage / answerCallbackQuery / editMessageText). Failures
/// are logged and reported as <c>false</c>, never thrown — a blocked bot or a Telegram hiccup
/// must not take down the poller or the webhook pipeline.
/// </summary>
public sealed class TelegramBotClient(
    HttpClient http,
    IOptions<TelegramAuthOptions> auth,
    ILogger<TelegramBotClient> logger) : ITelegramBotClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<bool> SendMessageAsync(
        long chatId,
        string text,
        IReadOnlyList<BotButton>? buttons = null,
        CancellationToken ct = default) =>
        CallAsync("sendMessage", new
        {
            chat_id = chatId,
            text,
            reply_markup = InlineKeyboard(buttons),
        }, ct);

    public Task<bool> AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken ct = default) =>
        CallAsync("answerCallbackQuery", new { callback_query_id = callbackQueryId, text }, ct);

    /// <summary>Replaces the message text and drops its inline keyboard.</summary>
    public Task<bool> EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken ct = default) =>
        CallAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text }, ct);

    public Task<bool> SetWebhookAsync(string url, string secretToken, CancellationToken ct = default) =>
        CallAsync("setWebhook", new
        {
            url,
            secret_token = secretToken,
            allowed_updates = new[] { "callback_query" },
        }, ct);

    private async Task<bool> CallAsync(string method, object payload, CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync(
                $"bot{auth.Value.BotToken}/{method}", payload, Json, ct);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning(
                "Telegram Bot API {Method} failed with {StatusCode}: {Body}",
                method, (int)response.StatusCode, body);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Telegram Bot API {Method} call failed", method);
            return false;
        }
    }

    private static object? InlineKeyboard(IReadOnlyList<BotButton>? buttons) =>
        buttons is null or []
            ? null
            : new
            {
                // One button per row — labels with emoji get tight on one line.
                inline_keyboard = buttons
                    .Select(b => new[] { new { text = b.Text, callback_data = b.CallbackData } })
                    .ToArray(),
            };
}
