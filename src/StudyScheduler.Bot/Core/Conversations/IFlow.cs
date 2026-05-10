namespace StudyScheduler.Bot.Core.Conversations;

public interface IFlow<TState> where TState : IFlowState, new()
{
    string FirstStep { get; }
    Task OnCompleteAsync(CompletionContext<TState> ctx);
}