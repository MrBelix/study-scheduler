using StudyScheduler.API.Features.Notifications;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>
/// Records every send and every callback answer and returns a configurable
/// <see cref="TelegramSendResult"/>, so a runner test can assert what was sent (chat, text, buttons)
/// and drive the persistence branch by outcome, and a webhook test can assert the button was answered.
/// </summary>
internal sealed class FakeNotificationSender : INotificationSender
{
    public TelegramSendResult Result { get; set; } = TelegramSendResult.Delivered;

    public List<SentMessage> Sent { get; } = [];

    public List<AnsweredCallback> Answered { get; } = [];

    public Task<TelegramSendResult> SendAsync(
        long chatId, string text, IReadOnlyList<NotificationButton> buttons, CancellationToken ct = default)
    {
        Sent.Add(new SentMessage(chatId, text, buttons));
        return Task.FromResult(Result);
    }

    public Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken ct = default)
    {
        Answered.Add(new AnsweredCallback(callbackQueryId, text));
        return Task.CompletedTask;
    }

    public sealed record SentMessage(long ChatId, string Text, IReadOnlyList<NotificationButton> Buttons);

    public sealed record AnsweredCallback(string CallbackQueryId, string? Text);
}
