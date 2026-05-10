using Telegram.Bot;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Conversations;

public interface IFlowEngine
{
    Task HandleAsync(ITelegramBotClient bot, Message message);
}