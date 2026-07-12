using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Registers the bot's webhook with Telegram at startup. When the URL or secret are unconfigured the
/// webhook is disabled and this no-ops. Otherwise it retries every 30s until Telegram accepts the
/// registration, so a transient outage at boot doesn't leave the bot permanently unhooked. Shutdown
/// cancels cleanly.
/// </summary>
public sealed class TelegramWebhookRegistrar(
    ITelegramBotClient bot,
    IOptions<NotificationsOptions> options,
    ILogger<TelegramWebhookRegistrar> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = options.Value.WebhookUrl;
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(secret))
        {
            logger.LogInformation("Telegram webhook not configured; skipping registration (poller-only mode)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await bot.SetWebhook(url, secretToken: secret, cancellationToken: stoppingToken);
                logger.LogInformation("Telegram webhook registered at {WebhookUrl}", url);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to register Telegram webhook; retrying in {Delay}", RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
