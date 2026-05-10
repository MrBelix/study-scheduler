using Microsoft.EntityFrameworkCore;
using StudyScheduler.Bot.Core;
using StudyScheduler.Bot.Core.Messages;
using StudyScheduler.Bot.Core.Routing;
using Telegram.Bot;

namespace StudyScheduler.Bot.Handlers.Students;

[Callback("students:list")]
public sealed class StudentListCallback(AppDbContext dbContext) : ICallbackHandler
{
    public async Task HandleAsync(ITelegramBotClient bot, CallbackContext ctx)
    {
        var students = await dbContext.Students.ToListAsync();
        var msg = StudentMessages.List(students);

        await bot.EditMessageText(ctx.ChatId, ctx.MessageId, msg.Text,
            replyMarkup: msg.Keyboard, parseMode: msg.ParseMode);
        await bot.AnswerCallbackQuery(ctx.CallbackQueryId);
    }
}