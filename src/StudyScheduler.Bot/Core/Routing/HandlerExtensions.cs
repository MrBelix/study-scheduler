using System.Reflection;

namespace StudyScheduler.Bot.Core.Routing;

public static class HandlerExtensions
{
    public static IServiceCollection AddBotHandlersFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        var handlerTypes = TelegramUpdateHandler.Commands.Values.AsEnumerable()
            .Concat(TelegramUpdateHandler.Callbacks.Values.Select(x => x.Type));

        foreach (var type in handlerTypes)
            services.AddScoped(type);

        return services;
    }
}