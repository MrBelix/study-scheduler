using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StudyScheduler.Bot;

public sealed class BotWorker : BackgroundService
{
    private readonly TelegramBotClient _botClient;
    private readonly ILogger<BotWorker> _logger;

    public BotWorker(IConfiguration configuration, ILogger<BotWorker> logger)
    {
        _logger = logger;

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
        _botClient.OnMessage += HandleMessageAsync;
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
            _botClient.OnMessage -= HandleMessageAsync;
            _botClient.OnError -= HandleErrorAsync;
        }
    }

    private async Task HandleMessageAsync(Message message, UpdateType type)
    {
        if (message.Text is not { } messageText) return;

        var chatId = message.Chat.Id;
        _logger.LogInformation("Received message: '{Text}' from chat {ChatId}.", messageText, chatId);

        await _botClient.SendMessage(
            chatId: chatId,
            text: $"Hello! I'm your StudyScheduler. You wrote: {messageText}");
    }

    private Task HandleErrorAsync(Exception exception, HandleErrorSource source)
    {
        _logger.LogError(exception, "Telegram API error ({Source}).", source);
        return Task.CompletedTask;
    }
}