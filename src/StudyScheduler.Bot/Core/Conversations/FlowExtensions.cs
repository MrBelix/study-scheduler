using System.Reflection;

namespace StudyScheduler.Bot.Core.Conversations;

public static class FlowExtensions
{
    public static IServiceCollection AddFlowInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<FlowRegistry>();
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddScoped<FlowDispatcher>();
        return services;
    }

    public static IServiceCollection AddFlowsFromAssembly(
        this IServiceCollection services, 
        Assembly assembly)
    {
        var flowInterfaceOpenGeneric = typeof(IFlow<>);
        var stepInterfaceOpenGeneric = typeof(IFlowStep<>);
        var engineOpenGeneric = typeof(FlowEngine<>);

        // 1. Знаходимо всі класи з [Flow] атрибутом, що реалізують IFlow<TState>
        var flowTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && t.GetCustomAttribute<FlowAttribute>() is not null
                && t.GetInterfaces().Any(IsFlowInterface));

        foreach (var flowType in flowTypes)
        {
            var attr = flowType.GetCustomAttribute<FlowAttribute>()!;
            var stateType = ExtractStateType(flowType);
            
            // 2. Реєструємо сам flow як IFlow<TState>
            var flowInterface = flowInterfaceOpenGeneric.MakeGenericType(stateType);
            services.AddScoped(flowInterface, flowType);

            // 3. Реєструємо FlowEngine<TState>
            var engineType = engineOpenGeneric.MakeGenericType(stateType);
            services.AddScoped(engineType);
            services.AddScoped(typeof(IFlowEngine), engineType);

            // 4. Запис у FlowRegistry
            services.AddSingleton(new FlowRegistration(attr.Name, engineType));
        }

        // 5. Реєструємо ВСІ кроки IFlowStep<TState>
        var stepTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && t.GetInterfaces().Any(IsStepInterface));

        foreach (var stepType in stepTypes)
        {
            var stepInterface = stepType.GetInterfaces().First(IsStepInterface);
            services.AddScoped(stepInterface, stepType);
        }

        return services;

        // Локальні предикати
        static bool IsFlowInterface(Type i) =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFlow<>);

        static bool IsStepInterface(Type i) =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFlowStep<>);

        static Type ExtractStateType(Type flowType) =>
            flowType.GetInterfaces()
                .First(IsFlowInterface)
                .GetGenericArguments()[0];
    }
}