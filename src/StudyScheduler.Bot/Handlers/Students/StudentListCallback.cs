using Microsoft.EntityFrameworkCore;
using StudyScheduler.Bot.Core;
using StudyScheduler.Bot.Core.Entities;
using StudyScheduler.Bot.Core.Routing;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace StudyScheduler.Bot.Handlers.Students;

[Callback("students:list")]
public sealed class StudentListCallback(AppDbContext dbContext) : ICallbackHandler
{
    public async Task HandleAsync(ITelegramBotClient bot, CallbackContext ctx)
    {
        var list = await dbContext.Students.ToListAsync();

        var (text, keyboard) = list.Count == 0
            ? RenderEmpty()
            : RenderList(list);

        await bot.EditMessageText(ctx.ChatId, ctx.MessageId, text, 
            replyMarkup: keyboard, parseMode: ParseMode.Markdown);

        await bot.AnswerCallbackQuery(ctx.CallbackQueryId);
    }

    private static (string, InlineKeyboardMarkup) RenderEmpty()
    {
        var text = "👨‍🎓 *Ваші учні*\n\nПоки що нікого немає.";
        var keyboard = new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("➕ Додати учня", "student:add")],
            [InlineKeyboardButton.WithCallbackData("⬅️ Меню", "menu:main")]
        ]);
        return (text, keyboard);
    }

    private static (string, InlineKeyboardMarkup) RenderList(IReadOnlyList<Student> list)
    {
        var text = $"👨‍🎓 *Ваші учні* ({list.Count})";

        var rows = list
            .Select(s => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{s.Name}",
                    $"student:view:{s.Id}")
            })
            .Append([InlineKeyboardButton.WithCallbackData("➕ Додати учня", "student:add")])
            .Append([InlineKeyboardButton.WithCallbackData("⬅️ Меню", "menu:main")])
            .ToArray();

        return (text, new InlineKeyboardMarkup(rows));
    }
}