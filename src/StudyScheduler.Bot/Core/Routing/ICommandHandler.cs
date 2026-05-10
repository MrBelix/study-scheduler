using Telegram.Bot;

namespace StudyScheduler.Bot.Core.Routing;

public interface ICommandHandler
{
    Task HandleAsync(ITelegramBotClient bot, CommandContext context);
}