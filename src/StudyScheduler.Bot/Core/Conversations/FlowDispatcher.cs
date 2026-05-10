using Telegram.Bot;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Conversations;

public sealed class FlowDispatcher(
    IConversationStore store,
    FlowRegistry registry,
    IServiceProvider services,
    ILogger<FlowDispatcher> logger)
{
    public async Task<bool> TryHandleAsync(ITelegramBotClient bot, Message message)
    {
        var activeFlow = await store.GetActiveFlowAsync(message.Chat.Id);
        if (activeFlow is null) return false;

        var engineType = registry.GetEngineType(activeFlow);
        if (engineType is null)
        {
            logger.LogWarning("No engine registered for flow {Flow}", activeFlow);
            return false;
        }

        var engine = (IFlowEngine)services.GetRequiredService(engineType);
        await engine.HandleAsync(bot, message);
        return true;
    }
}