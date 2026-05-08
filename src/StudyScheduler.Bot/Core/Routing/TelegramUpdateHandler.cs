using System.Collections.Frozen;
using System.Reflection;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot.Core.Routing;

public sealed class TelegramUpdateHandler
{
    internal static readonly FrozenDictionary<string, Type> Commands = BuildRoutes<ICommandHandler, CommandAttribute>(a => a.Command);
    internal static readonly FrozenDictionary<string, Type> Callbacks = BuildRoutes<ICallbackHandler, CallbackAttribute>(a => a.Callback);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(IServiceScopeFactory scopeFactory, ILogger<TelegramUpdateHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private static FrozenDictionary<string, Type> BuildRoutes<THandler, TAttr>(Func<TAttr, string> keySelector)
        where TAttr : Attribute
    {
        return Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(THandler).IsAssignableFrom(t))
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<TAttr>()))
            .Where(x => x.Attr is not null)
            .ToFrozenDictionary(x => keySelector(x.Attr!), x => x.Type);
    }

    public async Task HandleAsync(ITelegramBotClient bot, Update update)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        switch (update)
        {
            case { Message: { Text: { } text } message }:
                var commandKey = text.Split(' ')[0];
                if (Commands.TryGetValue(commandKey, out var commandType))
                    await ((ICommandHandler)ActivatorUtilities.CreateInstance(sp, commandType)).HandleAsync(bot, message);
                else
                    _logger.LogWarning("Unknown command: {Command}", commandKey);
                break;

            case { CallbackQuery: { Data: { } data } query }:
                if (Callbacks.TryGetValue(data, out var callbackType))
                    await ((ICallbackHandler)ActivatorUtilities.CreateInstance(sp, callbackType)).HandleAsync(bot, query);
                else
                    _logger.LogWarning("Unknown callback: {Callback}", data);
                break;

            default:
                _logger.LogDebug("Unhandled update type: {UpdateType}", update.Type);
                break;
        }
    }
}