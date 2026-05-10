using StudyScheduler.Bot.Core.Conversations;
using StudyScheduler.Bot.Core.Routing;
using StudyScheduler.Bot.Flows.AddStudent;
using Telegram.Bot;

namespace StudyScheduler.Bot.Handlers.Students;

[Callback("students:add")]
public sealed class StudentAddCallback(IConversationStore store) : ICallbackHandler
{
    public async Task HandleAsync(ITelegramBotClient bot, CallbackContext context)
    {
        var state = new AddStudentState(); // дефолтний CurrentStep = AskName
        await store.SaveAsync(context.ChatId, state);

        await bot.EditMessageText(context.ChatId, context.MessageId, "👤 Введіть ім'я учня:");
        await bot.AnswerCallbackQuery(context.CallbackQueryId);
    }
}