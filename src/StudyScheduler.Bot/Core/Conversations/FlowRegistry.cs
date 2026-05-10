using System.Collections.Frozen;

namespace StudyScheduler.Bot.Core.Conversations;

public sealed class FlowRegistry
{
    private readonly FrozenDictionary<string, Type> _flowToEngine;

    public FlowRegistry(IEnumerable<FlowRegistration> registrations, ILogger<FlowRegistry> logger)
    {
        var list = registrations.ToList();

        // Перевірка унікальності назв
        var duplicates = list
            .GroupBy(r => r.FlowName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Duplicate flow names: {string.Join(", ", duplicates)}");

        _flowToEngine = list.ToFrozenDictionary(r => r.FlowName, r => r.EngineType);

        logger.LogInformation("Registered {Count} flows: {Names}", 
            _flowToEngine.Count, string.Join(", ", _flowToEngine.Keys));
    }

    public Type? GetEngineType(string flowName) =>
        _flowToEngine.GetValueOrDefault(flowName);
}