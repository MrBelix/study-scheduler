using StudyScheduler.Bot.Core.Routing;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.Bot.Handlers;

[Command("/start")]
public class StartCommand : ICommandHandler
{
    public async Task HandleAsync(ITelegramBotClient bot, Message message)
    {
        var keyboard = new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("👨‍🎓 Мої учні", "action_students")],
            [InlineKeyboardButton.WithCallbackData("➕ Додати", "action_add_student")]
        ]);

        await bot.SendMessage(message.Chat.Id, "👋 Привіт! Я твій StudyScheduler.", replyMarkup: keyboard);
    }
}