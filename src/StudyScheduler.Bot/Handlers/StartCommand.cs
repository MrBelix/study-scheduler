using StudyScheduler.Bot.Core.Routing;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.Bot.Handlers;

[Command("/start")]
public class StartCommand : ICommandHandler
{
    public async Task HandleAsync(ITelegramBotClient bot, CommandContext context)
    {
        var keyboard = new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("👨‍🎓 Мої учні", "students:list")],
            [InlineKeyboardButton.WithCallbackData("➕ Додати", "students:add")]
        ]);

        await bot.SendMessage(context.Message.Chat.Id, "👋 Привіт! Я твій StudyScheduler.", replyMarkup: keyboard);
    }
}