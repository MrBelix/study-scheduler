using StudyScheduler.Bot.Core;
using StudyScheduler.Bot.Core.Conversations;
using StudyScheduler.Bot.Core.Entities;
using StudyScheduler.Bot.Core.Messages;
using Telegram.Bot;

namespace StudyScheduler.Bot.Flows.AddStudent;

[Flow(Name)]
public sealed class AddStudentFlow(AppDbContext dbContext) : IFlow<AddStudentState>
{
    public const string Name = "add_student";

    public string FirstStep => AddStudentState.AskName;

    public async Task OnCompleteAsync(CompletionContext<AddStudentState> ctx)
    {
        var student = Student.Create(ctx.State.Name!);
        dbContext.Students.Add(student);
        await dbContext.SaveChangesAsync();

        var msg = StudentMessages.Added(student, ctx.State.PricePerLesson);
        await ctx.Bot.SendMessage(ctx.ChatId, msg.Text, replyMarkup: msg.Keyboard);
    }
}