using Telegram.Bot;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Routing;

public interface ICallbackHandler
{
    Task HandleAsync(ITelegramBotClient bot, CallbackQuery query);
}