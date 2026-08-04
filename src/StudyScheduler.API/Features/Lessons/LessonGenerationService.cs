namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// In-process loop that drives one <see cref="LessonGenerator.ExtendAllAsync"/> pass a day, so the
/// <see cref="PlanningHorizon"/> keeps rolling forward without anyone touching a series. The first
/// pass runs shortly after startup, which is also what backfills every series created before eager
/// generation existed. Each tick runs in its own DI scope (fresh repositories and unit of work); a
/// failing tick is caught and logged but never stops the loop. Shutdown is cooperative via the
/// host-supplied stopping token.
/// </summary>
public sealed class LessonGenerationService(
    IServiceScopeFactory scopeFactory,
    ILogger<LessonGenerationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // A day's worth of new horizon is not urgent, but a restart must not leave a fresh deployment (or
    // a series created just before it) waiting a whole interval — the first pass runs once startup
    // has settled.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Lesson generation started; interval {Interval}", Interval);

        // The timer starts on the short startup delay and widens to the real interval after the
        // first tick, so "run soon after boot, then daily" needs no separate delay step.
        using var timer = new PeriodicTimer(StartupDelay);
        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            timer.Period = Interval;
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
                // Isolate the tick: a transient failure must not tear down generation for good.
                logger.LogError(ex, "Lesson generation tick failed; will retry next interval");
            }
        }

        logger.LogInformation("Lesson generation stopping");
    }

    /// <summary>
    /// Runs a single pass: opens a DI scope, resolves the generator and awaits it. Factored out so
    /// the tick body can be exercised in tests without the timer.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var generator = scope.ServiceProvider.GetRequiredService<LessonGenerator>();
        await generator.ExtendAllAsync(ct);
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
