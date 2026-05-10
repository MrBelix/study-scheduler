using System.Collections.Frozen;
using System.Reflection;
using StudyScheduler.Bot.Core.Conversations;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StudyScheduler.Bot.Core.Routing;

public sealed class TelegramUpdateHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramUpdateHandler> logger)
{
    internal static readonly FrozenDictionary<string, Type> Commands =
        BuildRoutes<ICommandHandler, CommandAttribute>(a => a.Command);
    internal static readonly FrozenDictionary<string, (Type Type, CallbackTemplate Template)> Callbacks =
        BuildCallbackRoutes();

    private readonly FrozenDictionary<UpdateType, Func<ITelegramBotClient, Update, IServiceProvider, Task>> _handlers =
        new Dictionary<UpdateType, Func<ITelegramBotClient, Update, IServiceProvider, Task>>
        {
            [UpdateType.Message]       = HandleMessageAsync,
            [UpdateType.CallbackQuery] = HandleCallbackAsync,
        }.ToFrozenDictionary();

    public async Task HandleAsync(ITelegramBotClient bot, Update update)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        if (_handlers.TryGetValue(update.Type, out var handler))
            await handler(bot, update, sp);
        else
            logger.LogDebug("Unhandled update type: {UpdateType}", update.Type);
    }

    private static async Task HandleMessageAsync(ITelegramBotClient bot, Update update, IServiceProvider sp)
    {
        if (update.Message?.Text is not { } text) return;

        var commandKey = text.Split(' ')[0];
        if (Commands.TryGetValue(commandKey, out var type))
        {
            var context = new CommandContext(update.Message);
            await ((ICommandHandler)sp.GetRequiredService(type)).HandleAsync(bot, context);
            return;
        }

        var dispatcher = sp.GetRequiredService<FlowDispatcher>();
        await dispatcher.TryHandleAsync(bot, update.Message!);
    }

    private static async Task HandleCallbackAsync(ITelegramBotClient bot, Update update, IServiceProvider sp)
    {
        if (update.CallbackQuery?.Data is not { } raw) return;

        var routeKey = ExtractRouteKey(raw);
        if (!Callbacks.TryGetValue(routeKey, out var route)) return;

        var data = CallbackData.Parse(raw, route.Template);
        var context = new CallbackContext(update.CallbackQuery, data);

        await ((ICallbackHandler)sp.GetRequiredService(route.Type))
            .HandleAsync(bot, context);
    }
    
    private static string ExtractRouteKey(string raw)
    {
        var first = raw.IndexOf(':');
        if (first == -1) return raw;
        
        var second = raw.IndexOf(':', first + 1);
        return second == -1 ? raw : raw[..second];
    }


    private static FrozenDictionary<string, Type> BuildRoutes<THandler, TAttr>(
        Func<TAttr, string> keySelector) where TAttr : Attribute
    {
        return Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(THandler).IsAssignableFrom(t))
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<TAttr>()))
            .Where(x => x.Attr is not null)
            .ToFrozenDictionary(x => keySelector(x.Attr!), x => x.Type);
    }
    
    private static FrozenDictionary<string, (Type, CallbackTemplate)> BuildCallbackRoutes()
    {
        return Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICallbackHandler).IsAssignableFrom(t))
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<CallbackAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x =>
            {
                var template = CallbackTemplate.Parse(x.Attr!.Template);
                return (template.RouteKey, (x.Type, template));
            })
            .ToFrozenDictionary(x => x.RouteKey, x => x.Item2);
    }
}