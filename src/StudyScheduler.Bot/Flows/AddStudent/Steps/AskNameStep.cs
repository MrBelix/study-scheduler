using Microsoft.EntityFrameworkCore;
using StudyScheduler.Bot.Core;
using StudyScheduler.Bot.Core.Conversations;
using Telegram.Bot;

namespace StudyScheduler.Bot.Flows.AddStudent.Steps;

public sealed class AskNameStep(AppDbContext dbContext) : IFlowStep<AddStudentState>
{
    public string Name => AddStudentState.AskName;

    public async Task<StepResult> HandleAsync(StepContext<AddStudentState> ctx)
    {
        var text = ctx.Message.Text?.Trim() ?? "";

        if (text.Length < 2)
        {
            await ctx.Bot.SendMessage(ctx.ChatId, "❌ Ім'я занадто коротке");
            return new StepResult.Repeat("name_too_short");
        }

        var exists = await dbContext.Students.AnyAsync(s => s.Name == text);
        if (exists)
        {
            await ctx.Bot.SendMessage(ctx.ChatId, $"❌ Учень '{text}' вже існує");
            return new StepResult.Repeat("name_duplicate");
        }

        ctx.State.Name = text;
        return new StepResult.Next(AddStudentState.AskPrice);
    }
}