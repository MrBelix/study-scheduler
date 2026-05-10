using StudyScheduler.Bot.Core.Conversations;
using StudyScheduler.Bot.Core.Messages;
using StudyScheduler.Bot.Core.Routing;
using StudyScheduler.Bot.Flows.AddStudent;
using Telegram.Bot;

namespace StudyScheduler.Bot.Handlers.Students;

[Callback("students:add")]
public sealed class StudentAddCallback(IConversationStore store) : ICallbackHandler
{
    public async Task HandleAsync(ITelegramBotClient bot, CallbackContext context)
    {
        await store.SaveAsync(context.ChatId, new AddStudentState());

        var msg = StudentMessages.AskName();
        await bot.EditMessageText(context.ChatId, context.MessageId, msg.Text);
        await bot.AnswerCallbackQuery(context.CallbackQueryId);
    }
}