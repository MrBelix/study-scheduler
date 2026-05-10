using System.Globalization;
using StudyScheduler.Bot.Core.Conversations;
using Telegram.Bot;

namespace StudyScheduler.Bot.Flows.AddStudent.Steps;

public sealed class AskPriceStep : IFlowStep<AddStudentState>
{
    public string Name => AddStudentState.AskPrice;

    public async Task OnEnterAsync(StepContext<AddStudentState> ctx) =>
        await ctx.Bot.SendMessage(ctx.ChatId, "💵 Ціна за урок (грн):");

    public async Task<StepResult> HandleAsync(StepContext<AddStudentState> ctx)
    {
        var text = ctx.Message.Text?.Trim() ?? "";

        if (!decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
            || price <= 0)
        {
            await ctx.Bot.SendMessage(ctx.ChatId, "❌ Введіть число більше 0");
            return new StepResult.Repeat("invalid_price");
        }

        ctx.State.PricePerLesson = price;
        return new StepResult.Complete();
    }
}