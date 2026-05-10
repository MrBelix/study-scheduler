using Telegram.Bot;

namespace StudyScheduler.Bot.Core.Routing;

public interface ICallbackHandler
{
    Task HandleAsync(ITelegramBotClient bot, CallbackContext context);
}