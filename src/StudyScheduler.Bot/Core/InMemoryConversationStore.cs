using System.Collections.Concurrent;
using StudyScheduler.Bot.Core.Conversations;

namespace StudyScheduler.Bot.Core;

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<long, IFlowState> _store = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(30);

    public Task<TState?> GetAsync<TState>(long chatId) where TState : class, IFlowState
    {
        if (!_store.TryGetValue(chatId, out var state))
            return Task.FromResult<TState?>(null);

        if (DateTime.UtcNow - state.CreatedAt > _ttl)
        {
            _store.TryRemove(chatId, out _);
            return Task.FromResult<TState?>(null);
        }

        // Type check замість string check
        return Task.FromResult(state as TState);
    }

    public Task SaveAsync(long chatId, IFlowState state)
    {
        _store[chatId] = state;
        return Task.CompletedTask;
    }

    public Task ClearAsync(long chatId)
    {
        _store.TryRemove(chatId, out _);
        return Task.CompletedTask;
    }

    public Task<string?> GetActiveFlowAsync(long chatId) =>
        Task.FromResult(_store.TryGetValue(chatId, out var state) ? state.FlowName : null);
}