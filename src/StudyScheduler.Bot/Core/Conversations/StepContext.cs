using Telegram.Bot;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Conversations;

public readonly record struct StepContext<TState>(
    ITelegramBotClient Bot,
    Message Message,
    TState State,
    long ChatId);