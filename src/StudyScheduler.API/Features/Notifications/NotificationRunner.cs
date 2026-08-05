using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// The Telegram calls one tick may still spend, shared by every tutor it touches so a large backlog is
/// drained across several ticks instead of tripping the platform's rate limits. Every send AND every
/// edit spends one. Mutable and single-threaded by design: it lives exactly as long as the tick does.
/// </summary>
public sealed class TelegramCallBudget(int max)
{
    private int _remaining = max;
    private bool _reported;

    /// <summary>The ceiling this tick started with — the number worth naming in the warning.</summary>
    public int Max { get; } = max;

    public bool Exhausted => _remaining <= 0;

    /// <summary>Spends one call; false once the tick's ceiling is reached.</summary>
    public bool TryConsume()
    {
        if (_remaining <= 0)
            return false;

        _remaining--;
        return true;
    }

    /// <summary>True the FIRST time the tick notices it ran out, so the warning is logged once.</summary>
    public bool ShouldReportExhaustion()
    {
        if (_reported)
            return false;

        _reported = true;
        return true;
    }
}

/// <summary>
/// One tick of notification work. For every tutor it touches, in this order: RECONCILE what is already
/// on screen (<see cref="NotificationReconciler"/>), PLAN what owes a first send
/// (<see cref="NotificationPlanner"/>), then DELIVER it. Reconciliation comes first so a message that
/// was retired this tick — a reminder whose lesson moved, say — frees the planner to open its
/// replacement in the very same pass.
/// The tick has no tenant of its own: it reads the notifiable profiles across all tutors, adds the
/// tutors who still own a live message even though they have since opted out of everything (their
/// keyboards must still be retired), then makes each tutor the scope's tenant before touching anything
/// of theirs — which is what scopes every lesson, dispatch and student read below to that one tutor.
/// Each send and each row mutation is committed on its own, so one blocked chat never blocks another;
/// a transient failure writes nothing and is retried against the same id next tick. A 403 flips the
/// tutor's bot flag off and abandons the rest of their queue. Per-tutor failures are isolated.
/// </summary>
public sealed class NotificationRunner(
    ITutorProfileRepository profiles,
    ILessonRepository lessons,
    INotificationDispatchRepository dispatches,
    INotificationSender sender,
    NotificationPlanner planner,
    NotificationReconciler reconciler,
    NotificationViewBuilder views,
    NotificationRenderer renderer,
    IOptions<NotificationsOptions> options,
    IUnitOfWork uow,
    ITutorScope tenant,
    TimeProvider clock,
    ILogger<NotificationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var budget = new TelegramCallBudget(options.Value.MaxTelegramCallsPerTick);

        foreach (var profile in await CollectTutorsAsync(ct))
        {
            if (StopOnExhaustedBudget(budget))
                break;

            try
            {
                await RunForTutorAsync(profile, budget, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One tutor's data anomaly or transport blow-up must not abort the whole tick.
                logger.LogError(ex, "Notification run failed for tutor {TutorId}", profile.TelegramUserId);
            }
        }
    }

    /// <summary>
    /// Every tutor this tick has business with: the ones who opted into a notification, plus the ones
    /// who still own a live message even though they have since turned everything off — otherwise a
    /// keyboard would sit there live forever. De-duplicated by tutor id, opt-ins first.
    /// </summary>
    private async Task<IReadOnlyList<TutorProfile>> CollectTutorsAsync(CancellationToken ct)
    {
        var queue = new List<TutorProfile>(await profiles.GetNotifiableAcrossAllTutorsAsync(ct));
        var seen = queue.Select(p => p.TelegramUserId).ToHashSet();

        foreach (var tutorId in await dispatches.GetTutorsWithLiveDispatchesAcrossAllTutorsAsync(ct))
        {
            if (!seen.Add(tutorId))
                continue;

            // A profile is keyed by its tutor, so reading it means becoming that tutor first.
            tenant.SetForBackground(tutorId);
            if (await profiles.GetAsync(ct) is { BotReachable: true } profile)
                queue.Add(profile);
        }

        return queue;
    }

    private async Task RunForTutorAsync(
        TutorProfile profile, TelegramCallBudget budget, DateTimeOffset now, CancellationToken ct)
    {
        // From here on the scope IS this tutor: every lesson, dispatch and student read below is theirs.
        tenant.SetForBackground(profile.TelegramUserId);

        await reconciler.ReconcileAsync(profile, budget, ct);
        if (!profile.BotReachable)
        {
            // Reconciliation hit a 403: persist the flag and drop this tutor for the tick.
            profiles.Update(profile);
            await uow.SaveChangesAsync(ct);
            return;
        }

        if (StopOnExhaustedBudget(budget))
            return;

        var zone = profile.TimeZone;
        var today = LocalDateOf(now, zone);
        var yesterday = today.AddDays(-1);

        // One range read covers both local days; a lesson belongs to the date its START falls on, so a
        // lesson spilling in from the previous day is dropped from today and stays with yesterday.
        var dayRange = await lessons.GetInRangeAsync(
            WallClock.ToUtc(yesterday, TimeOnly.MinValue, zone),
            WallClock.ToUtc(today.AddDays(1), TimeOnly.MinValue, zone),
            ct: ct);
        var byLocalDate = new Dictionary<DateOnly, IReadOnlyList<Lesson>>
        {
            [yesterday] = [.. dayRange.Where(l => LocalDateOf(l.StartUtc, zone) == yesterday)],
            [today] = [.. dayRange.Where(l => LocalDateOf(l.StartUtc, zone) == today)],
        };

        IReadOnlyList<Lesson> reminderWindow = profile.RemindMinutes is { } remind
            ? await lessons.GetInRangeAsync(now, now.AddMinutes(remind), ct: ct)
            : [];
        var reminderLessons = reminderWindow.ToDictionary(l => l.Id);

        // Read AFTER reconciliation, so a row it just retracted no longer blocks a replacement.
        var live = await dispatches.GetLiveAsync(ct: ct);
        var grace = options.Value.SummaryGraceMinutes;

        var due = new List<DueNotification>(
            planner.Plan(profile, byLocalDate[today], reminderWindow, live, now, grace));
        // Yesterday can still owe its summary while it spills past local midnight; its reminders and
        // its agenda are settled business, so it is planned with an empty reminder window.
        due.AddRange(planner.Plan(profile, byLocalDate[yesterday], [], live, now, grace));

        foreach (var item in due)
        {
            if (StopOnExhaustedBudget(budget))
                return;

            // A 403 disables the tutor's bot; there is no point sending the rest of their queue to an
            // unreachable chat this tick.
            if (!await DeliverAsync(profile, item, reminderLessons, byLocalDate, budget, now, ct))
                return;
        }
    }

    /// <summary>
    /// Renders and sends one due message and settles the outcome. Returns <c>true</c> when the tutor's
    /// queue may keep draining and <c>false</c> when it must stop (a 403, or the tick's call budget).
    /// </summary>
    private async Task<bool> DeliverAsync(
        TutorProfile profile,
        DueNotification due,
        IReadOnlyDictionary<Guid, Lesson> reminderLessons,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<Lesson>> byLocalDate,
        TelegramCallBudget budget,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var chatId = profile.TelegramUserId;

        RenderedMessage message;
        DateTimeOffset expiresAtUtc;

        switch (due.Kind)
        {
            case NotificationKind.Reminder:
            {
                var lessonId = due.LessonId!.Value;
                if (!reminderLessons.TryGetValue(lessonId, out var lesson))
                    return true;

                var view = await views.BuildReminderAsync(lessonId, profile, previous: null, now, ct);
                if (view is null)
                    return true;

                message = renderer.Reminder(view);
                // A reminder stops owing anything once its lesson ends.
                expiresAtUtc = lesson.EndUtc;
                break;
            }

            case NotificationKind.DayAgenda:
            {
                var localDate = due.LocalDate!.Value;
                message = renderer.Agenda(
                    await views.BuildAgendaAsync(localDate, profile, previous: null, now, ct));
                expiresAtUtc = DigestExpiry(localDate, byLocalDate, profile.TimeZone, now);
                break;
            }

            case NotificationKind.DaySummary:
            {
                var localDate = due.LocalDate!.Value;
                message = renderer.Summary(await views.BuildSummaryAsync(
                    localDate, profile, focusedLessonId: null, expanded: false, now, ct));
                expiresAtUtc = DigestExpiry(localDate, byLocalDate, profile.TimeZone, now);
                break;
            }

            default:
                return true;
        }

        if (!budget.TryConsume())
            return false;

        var outcome = await sender.SendAsync(chatId, message.Text, message.Rows, ct);

        switch (outcome.Result)
        {
            case TelegramSendResult.TransientFailure:
                // Write nothing: the very same message is retried against the same target next tick.
                logger.LogWarning(
                    "Transient failure sending {Kind} to tutor {TutorId}; will retry next tick",
                    due.Kind, chatId);
                return true;

            case TelegramSendResult.Unreachable:
                // A 403: the tutor never started or blocked the bot. Flip the flag off and write no
                // dispatch, so the message can still go out once the bot is re-enabled in-window.
                profile.MarkBotUnreachable();
                profiles.Update(profile);
                await uow.SaveChangesAsync(ct);
                logger.LogWarning(
                    "Tutor {TutorId} chat unreachable (403) sending {Kind}; disabling bot and skipping the rest this tick",
                    chatId, due.Kind);
                return false;

            case TelegramSendResult.PermanentFailure:
                // A 400 will never be accepted: record the dispatch anyway so it cannot loop.
                logger.LogError(
                    "Permanent failure (400) sending {Kind} to tutor {TutorId}; recording the dispatch to avoid a retry loop",
                    due.Kind, chatId);
                break;

            case TelegramSendResult.Delivered:
                break;
        }

        // Delivered or PermanentFailure: the dispatch row IS the record of the send, the dedup the next
        // plan reads, and the snapshot every later re-render diffs against.
        dispatches.Add(due.Kind == NotificationKind.Reminder
            ? NotificationDispatch.ForReminder(
                due.LessonId!.Value, chatId, outcome.MessageId,
                message.Snapshot, message.ContentHash, expiresAtUtc, now)
            : NotificationDispatch.ForDay(
                due.Kind, due.LocalDate!.Value, chatId, outcome.MessageId,
                message.Snapshot, message.ContentHash, expiresAtUtc, now));
        await uow.SaveChangesAsync(ct);

        logger.LogInformation("Sent {Kind} to tutor {TutorId} ({Result})", due.Kind, chatId, outcome.Result);
        return true;
    }

    /// <summary>
    /// When a digest stops owing anything: the local midnight following the day it covers, stretched to
    /// that day's last lesson end. A day that spilled past midnight is summarised on the FOLLOWING local
    /// date, so the midnight after THAT one bounds it instead — an expiry already in the past would have
    /// the reconciler retire the message on the very next tick.
    /// </summary>
    private static DateTimeOffset DigestExpiry(
        DateOnly localDate,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<Lesson>> byLocalDate,
        TimeZoneInfo zone,
        DateTimeOffset now)
    {
        var midnightAfterDate = WallClock.ToUtc(localDate.AddDays(1), TimeOnly.MinValue, zone);
        var midnightAfterToday = WallClock.ToUtc(LocalDateOf(now, zone).AddDays(1), TimeOnly.MinValue, zone);
        var expiry = midnightAfterDate > midnightAfterToday ? midnightAfterDate : midnightAfterToday;

        if (byLocalDate.TryGetValue(localDate, out var dayLessons) && dayLessons.Count > 0)
        {
            var lastEnd = dayLessons.Max(l => l.EndUtc);
            if (lastEnd > expiry)
                expiry = lastEnd;
        }

        return expiry;
    }

    /// <summary>Logs the ceiling once and tells the caller to stop the tick cleanly.</summary>
    private bool StopOnExhaustedBudget(TelegramCallBudget budget)
    {
        if (!budget.Exhausted)
            return false;

        if (budget.ShouldReportExhaustion())
            logger.LogWarning(
                "Telegram call budget of {Max} exhausted; stopping this tick — the rest is picked up next interval",
                budget.Max);

        return true;
    }

    private static DateOnly LocalDateOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
