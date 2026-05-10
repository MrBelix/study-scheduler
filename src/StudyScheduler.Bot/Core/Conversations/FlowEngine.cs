using System.Collections.Frozen;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Conversations;

public sealed class FlowEngine<TState>(
    IFlow<TState> flow,
    IEnumerable<IFlowStep<TState>> steps,
    IConversationStore store,
    ILogger<FlowEngine<TState>> logger) : IFlowEngine
    where TState : class, IFlowState, new()
{
    private readonly FrozenDictionary<string, IFlowStep<TState>> _steps =
        steps.ToFrozenDictionary(s => s.Name);

    public async Task HandleAsync(ITelegramBotClient bot, Message message)
    {
        var chatId = message.Chat.Id;
        var state = await store.GetAsync<TState>(chatId);
        
        if (state is null)
        {
            logger.LogWarning("State not found for flow in chat {ChatId}", chatId);
            return;
        }

        if (!_steps.TryGetValue(state.CurrentStep, out var step))
        {
            logger.LogError("Unknown step {Step} in flow {Flow}", state.CurrentStep, state.FlowName);
            return;
        }

        var stepCtx = new StepContext<TState>(bot, message, state, chatId);
        var result = await step.HandleAsync(stepCtx);

        await ApplyResultAsync(result, bot, message, state, chatId);
    }

    private async Task ApplyResultAsync(
        StepResult result,
        ITelegramBotClient bot,
        Message message,
        TState state,
        long chatId)
    {
        switch (result)
        {
            case StepResult.Next next:
                state.CurrentStep = next.NextStep;
                await store.SaveAsync(chatId, state);
                await EnterStepAsync(next.NextStep, bot, message, state, chatId);
                break;

            case StepResult.Repeat:
                break;

            case StepResult.GoTo goTo:
                state.CurrentStep = goTo.Step;
                await store.SaveAsync(chatId, state);
                await EnterStepAsync(goTo.Step, bot, message, state, chatId);
                break;

            case StepResult.Complete:
                var completion = new CompletionContext<TState>(
                    bot, chatId, message.From!.Id, state);
                await flow.OnCompleteAsync(completion);  // ← flow має свої залежності
                await store.ClearAsync(chatId);
                break;

            case StepResult.Cancel:
                await store.ClearAsync(chatId);
                await bot.SendMessage(chatId, "❌ Скасовано");
                break;
        }
    }

    private async Task EnterStepAsync(string stepName, ITelegramBotClient bot, Message message, TState state, long chatId)
    {
        if (!_steps.TryGetValue(stepName, out var step))
        {
            logger.LogWarning("Cannot enter unknown step {Step}", stepName);
            return;
        }

        var ctx = new StepContext<TState>(bot, message, state, chatId);
        await step.OnEnterAsync(ctx);
    }
}