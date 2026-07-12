using Microsoft.Extensions.Options;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// In-process poller that drives one <see cref="NotificationRunner"/> tick on a fixed interval.
/// Each tick runs in its own DI scope so scoped dependencies (repositories, unit of work) get a
/// fresh lifetime. A failing tick is caught and logged but never stops the loop — the next tick
/// simply retries. Shutdown is cooperative via the host-supplied stopping token.
/// </summary>
public sealed class NotificationPollerService(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<NotificationPollerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.PollIntervalMinutes);
        logger.LogInformation("Notification poller started; interval {Interval}", interval);

        using var timer = new PeriodicTimer(interval);
        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Isolate the tick: a transient failure must not tear down the poller.
                logger.LogError(ex, "Notification poller tick failed; will retry next interval");
            }
        }

        logger.LogInformation("Notification poller stopping");
    }

    /// <summary>
    /// Runs a single poll tick: opens a DI scope, resolves the runner and awaits it. Factored out so
    /// the tick body can be exercised in tests without the timer.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<NotificationRunner>();
        await runner.RunAsync(ct);
    }

    // A clean stop cancels the wait; treat that as "no more ticks" rather than an error.
    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
