using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.Bot.Core.Messages;

public static class MenuMessages
{
    public static BotMessage Welcome() => new(
        "👋 Привіт! Я твій StudyScheduler.",
        new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("👨‍🎓 Мої учні", "students:list")],
            [InlineKeyboardButton.WithCallbackData("➕ Додати", "students:add")]
        ]));
}
