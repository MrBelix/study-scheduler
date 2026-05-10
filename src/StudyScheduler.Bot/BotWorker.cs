using StudyScheduler.Bot.Core.Routing;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace StudyScheduler.Bot;

public sealed class BotWorker : BackgroundService
{
    private readonly TelegramBotClient _botClient;
    private readonly TelegramUpdateHandler _updateHandler;
    private readonly ILogger<BotWorker> _logger;

    public BotWorker(
        IConfiguration configuration,
        TelegramUpdateHandler updateHandler,
        ILogger<BotWorker> logger)
    {
        _logger = logger;
        _updateHandler = updateHandler;

        var token = configuration["Telegram:Token"];

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Telegram token is missing. Make sure User Secrets are loaded and the app is running in Development mode.");
        }

        token = token.Trim();

        _botClient = new TelegramBotClient(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _botClient.OnUpdate += OnUpdateReceived;
        _botClient.OnError += HandleErrorAsync;

        var me = await _botClient.GetMe(stoppingToken);
        _logger.LogInformation("Bot @{Username} started and is waiting for messages.", me.Username);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            _botClient.OnUpdate -= OnUpdateReceived;
            _botClient.OnError -= HandleErrorAsync;
        }
    }

    private async Task OnUpdateReceived(Update update)
    {
        await _updateHandler.HandleAsync(_botClient, update);
    }

    private Task HandleErrorAsync(Exception exception, HandleErrorSource source)
    {
        _logger.LogError(exception, "Telegram API error ({Source}).", source);
        return Task.CompletedTask;
    }
}