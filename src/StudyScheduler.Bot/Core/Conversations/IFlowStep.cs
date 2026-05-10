namespace StudyScheduler.Bot.Core.Conversations;

public interface IFlowStep<TState> where TState : IFlowState
{
    string Name { get; }
    Task OnEnterAsync(StepContext<TState> ctx) => Task.CompletedTask;
    Task<StepResult> HandleAsync(StepContext<TState> ctx);
}