using StudyScheduler.API.Features.Notifications;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>
/// Records every send, callback answer and message edit and returns a configurable
/// <see cref="TelegramSendResult"/>, so a runner test can assert what was sent (chat, text, keyboard
/// rows) and drive the persistence branch by outcome, and a webhook test can assert the button was
/// answered and the message edited into a record. Every successful send hands back an increasing
/// message id, exactly as Telegram does — that id is the handle later edits need.
/// </summary>
internal sealed class FakeNotificationSender : INotificationSender
{
    public TelegramSendResult Result { get; set; } = TelegramSendResult.Delivered;

    public TelegramSendResult EditResult { get; set; } = TelegramSendResult.Delivered;

    /// <summary>The id the NEXT successful send returns; incremented after each one.</summary>
    public int NextMessageId { get; set; } = 1000;

    public List<SentMessage> Sent { get; } = [];

    public List<AnsweredCallback> Answered { get; } = [];

    public List<EditedMessage> Edited { get; } = [];

    public Task<TelegramSendOutcome> SendAsync(
        long chatId, string text, IReadOnlyList<NotificationButtonRow> rows, CancellationToken ct = default)
    {
        Sent.Add(new SentMessage(chatId, text, rows));
        return Task.FromResult(Result == TelegramSendResult.Delivered
            ? new TelegramSendOutcome(Result, NextMessageId++)
            : new TelegramSendOutcome(Result, null));
    }

    public Task<TelegramSendResult> EditMessageAsync(
        long chatId, int messageId, string text, IReadOnlyList<NotificationButtonRow> rows,
        CancellationToken ct = default)
    {
        Edited.Add(new EditedMessage(chatId, messageId, text, rows));
        return Task.FromResult(EditResult);
    }

    public Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken ct = default)
    {
        Answered.Add(new AnsweredCallback(callbackQueryId, text));
        return Task.CompletedTask;
    }

    public sealed record SentMessage(long ChatId, string Text, IReadOnlyList<NotificationButtonRow> Rows)
    {
        /// <summary>Every button of the message, row order preserved — the usual assertion surface.</summary>
        public IReadOnlyList<NotificationButton> Buttons => [.. Rows.SelectMany(r => r.Buttons)];
    }

    public sealed record AnsweredCallback(string CallbackQueryId, string? Text);

    public sealed record EditedMessage(
        long ChatId, int MessageId, string Text, IReadOnlyList<NotificationButtonRow> Rows);
}
