using Telegram.Bot;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Routing;

public interface ICommandHandler
{
    Task HandleAsync(ITelegramBotClient bot, Message message);
}