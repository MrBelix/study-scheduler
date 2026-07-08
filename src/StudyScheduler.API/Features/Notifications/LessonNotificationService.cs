using Microsoft.Extensions.Options;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Hosts the notification poller: registers the Telegram webhook once (when configured), then
/// runs a <see cref="LessonNotifier"/> tick every <see cref="NotificationsOptions.PollIntervalMinutes"/>.
/// Each tick gets a fresh scope (scoped DbContext); a failed tick is logged and the loop goes on.
/// </summary>
public sealed class LessonNotificationService(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<LessonNotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterWebhookAsync(stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.PollIntervalMinutes));
        logger.LogInformation("Lesson notification poller started (interval {Interval})", interval);

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await TickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LessonNotifier>().RunOnceAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Lesson notification tick failed");
        }
    }

    private async Task RegisterWebhookAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.WebhookUrl) || string.IsNullOrWhiteSpace(settings.WebhookSecret))
            return;

        using var scope = scopeFactory.CreateScope();
        var bot = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
        if (await bot.SetWebhookAsync(settings.WebhookUrl, settings.WebhookSecret, ct))
            logger.LogInformation("Telegram webhook registered at {WebhookUrl}", settings.WebhookUrl);
    }
}
