using StudyScheduler.Bot.Core;
using StudyScheduler.Bot.Core.Conversations;
using StudyScheduler.Bot.Core.Entities;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

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

        var keyboard = new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("👤 Картка", $"student:view:{student.Id}")],
            [InlineKeyboardButton.WithCallbackData("⬅️ До списку", "student:list")]
        ]);

        await ctx.Bot.SendMessage(ctx.ChatId, 
            $"✅ {student.Name} · {ctx.State.PricePerLesson} грн/урок", 
            replyMarkup: keyboard);
    }
}