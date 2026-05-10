using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.Bot.Core;

public sealed record BotMessage(
    string Text,
    InlineKeyboardMarkup? Keyboard = null,
    ParseMode ParseMode = ParseMode.None);
