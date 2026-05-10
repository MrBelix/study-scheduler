using Telegram.Bot;

namespace StudyScheduler.Bot.Core.Conversations;

public readonly record struct CompletionContext<TState>(
    ITelegramBotClient Bot,
    long ChatId,
    long UserId,
    TState State);