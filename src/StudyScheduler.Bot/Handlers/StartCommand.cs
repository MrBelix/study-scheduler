using StudyScheduler.Bot.Core.Messages;
using StudyScheduler.Bot.Core.Routing;
using Telegram.Bot;

namespace StudyScheduler.Bot.Handlers;

[Command("/start")]
public sealed class StartCommand : ICommandHandler
{
    public async Task HandleAsync(ITelegramBotClient bot, CommandContext context)
    {
        var msg = MenuMessages.Welcome();
        await bot.SendMessage(context.Message.Chat.Id, msg.Text, replyMarkup: msg.Keyboard);
    }
}