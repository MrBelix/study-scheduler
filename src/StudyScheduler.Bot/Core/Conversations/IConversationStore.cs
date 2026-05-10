namespace StudyScheduler.Bot.Core.Conversations;

public interface IConversationStore
{
    Task<TState?> GetAsync<TState>(long chatId) where TState : class, IFlowState;
    Task SaveAsync(long chatId, IFlowState state);
    Task ClearAsync(long chatId);
    Task<string?> GetActiveFlowAsync(long chatId);
}