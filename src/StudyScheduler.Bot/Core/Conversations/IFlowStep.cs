namespace StudyScheduler.Bot.Core.Conversations;

public interface IFlowStep<TState> where TState : IFlowState
{
    string Name { get; }
    Task<StepResult> HandleAsync(StepContext<TState> context);
}