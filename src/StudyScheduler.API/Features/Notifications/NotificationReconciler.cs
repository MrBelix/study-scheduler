using Microsoft.Extensions.Options;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Keeps every message already on screen truthful. Each live dispatch is re-rendered from CURRENT data
/// against the snapshot it was last rendered from; the resulting <see cref="RenderedMessage.ContentHash"/>
/// is compared with the stored one, so a re-render that changes nothing costs no Telegram call at all.
/// The lifecycle phase is part of the rendered text and therefore part of that hash, which is why a
/// reminder repaints itself into its "уже почався" form the moment the clock passes the lesson's start
/// with no data change whatsoever — no separate timer exists or is needed.
/// A message that can no longer change (a moved, cancelled, completed or deleted lesson, or any dispatch
/// past its <see cref="NotificationDispatch.ExpiresAtUtc"/>) gets ONE last edit with its keyboard
/// stripped and is then retracted. Retraction is what frees the planner to arm a brand-new reminder at
/// the new time — the partial unique index deliberately ignores retracted rows so it can.
/// The tenant is established by <see cref="NotificationRunner"/> before this runs: nothing here ever
/// touches the scope.
/// </summary>
public sealed class NotificationReconciler(
    INotificationDispatchRepository dispatches,
    NotificationViewBuilder views,
    NotificationRenderer renderer,
    INotificationSender sender,
    IUnitOfWork uow,
    TimeProvider clock,
    IOptions<NotificationsOptions> options,
    ILogger<NotificationReconciler> logger)
{
    /// <summary>
    /// Re-renders every live dispatch of the CURRENT tenant and returns how many Telegram calls it
    /// spent. A 403 on any edit flips <paramref name="profile"/>'s reachability off and abandons the
    /// rest of the queue — the caller sees <see cref="TutorProfile.BotReachable"/> go false, persists
    /// it and drops the tutor for this tick.
    /// </summary>
    public async Task<int> ReconcileAsync(
        TutorProfile profile, TelegramCallBudget budget, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var calls = 0;

        foreach (var row in await dispatches.GetLiveAsync(track: true, ct))
        {
            // GetLiveAsync is deliberately NOT bounded by ExpiresAtUtc: an expired row still owes one
            // final pass. The queue stays bounded anyway because that pass retracts it for good.
            var expired = row.ExpiresAtUtc <= now;

            if (row.MessageId is not { } messageId)
            {
                // Nothing on screen to rewrite (a send that failed permanently, or a row seeded by the
                // migration). It still blocks a duplicate until it expires, and never costs a call.
                if (expired)
                    await RetractAsync(row, now, ct);
                continue;
            }

            // A DaySummary row synced within the last poll interval is very likely a step-2 focus form
            // (or an expanded list) the tutor tapped into — s:/b:/m: deliberately do NOT persist that
            // navigation state on the row, so a re-render from here would always rebuild the plain list
            // and snap the tutor's screen back under them before they can act on it. Give the webhook's
            // own re-render one full interval to be the last word; an expired row still gets its final
            // pass regardless, since it can no longer be mid-navigation.
            if (!expired && row.Kind == NotificationKind.DaySummary
                && now - row.LastSyncedAtUtc < TimeSpan.FromMinutes(options.Value.PollIntervalMinutes))
                continue;

            if (await RenderAsync(row, profile, now, ct) is not { } rendered)
            {
                // Nothing renders any more: the lesson is gone and no snapshot remembers it either.
                await RetractAsync(row, now, ct);
                continue;
            }

            var (message, phaseTerminal) = rendered;
            var terminal = expired || phaseTerminal;
            if (!terminal && message.ContentHash == row.ContentHash)
                continue;

            if (!budget.TryConsume())
                return calls;

            calls++;
            // A terminal message keeps its text and loses its keyboard — there is nothing left to press.
            IReadOnlyList<NotificationButtonRow> rows = terminal ? [] : message.Rows;
            var result = await sender.EditMessageAsync(row.ChatId, messageId, message.Text, rows, ct);

            switch (result)
            {
                case TelegramSendResult.TransientFailure:
                    // Leave the row exactly as it is; the next tick re-renders and retries it.
                    logger.LogWarning(
                        "Transient failure editing dispatch {DispatchId} of tutor {TutorId}; retrying next tick",
                        row.Id, profile.TelegramUserId);
                    continue;

                case TelegramSendResult.Unreachable:
                    profile.MarkBotUnreachable();
                    logger.LogWarning(
                        "Tutor {TutorId} chat unreachable (403) editing dispatch {DispatchId}; abandoning their queue this tick",
                        profile.TelegramUserId, row.Id);
                    return calls;

                case TelegramSendResult.PermanentFailure:
                    // The message is gone (deleted, or too old to edit): stop trying to keep it truthful.
                    logger.LogWarning(
                        "Permanent failure editing dispatch {DispatchId} of tutor {TutorId}; retracting it",
                        row.Id, profile.TelegramUserId);
                    await RetractAsync(row, now, ct);
                    continue;

                case TelegramSendResult.Delivered:
                    break;
            }

            if (terminal)
            {
                await RetractAsync(row, now, ct);
                continue;
            }

            row.Resync(message.Snapshot, message.ContentHash, now);
            dispatches.Update(row);
            await uow.SaveChangesAsync(ct);
        }

        return calls;
    }

    /// <summary>
    /// The message this dispatch would produce from current data, plus whether it has reached a form
    /// that can never change again. Null when there is nothing left to render at all.
    /// </summary>
    private async Task<(RenderedMessage Message, bool Terminal)?> RenderAsync(
        NotificationDispatch row, TutorProfile profile, DateTimeOffset nowUtc, CancellationToken ct)
    {
        switch (row.Kind)
        {
            case NotificationKind.Reminder:
                var reminder = await views.BuildReminderAsync(row.LessonId!.Value, profile, row.Snapshot, nowUtc, ct);
                if (reminder is null)
                    return null;

                // Moved is terminal on purpose: the old message settles into "перенесено" and retires,
                // which lets the planner arm a fresh reminder for the new slot.
                var terminal = reminder.Phase
                    is ReminderPhase.Moved
                    or ReminderPhase.Cancelled
                    or ReminderPhase.Completed
                    or ReminderPhase.Removed;
                return (renderer.Reminder(reminder), terminal);

            case NotificationKind.DayAgenda:
                var agenda = await views.BuildAgendaAsync(row.LocalDate!.Value, profile, row.Snapshot, nowUtc, ct);
                if (agenda.Lines.Count == 0)
                    return null;

                // A digest stays editable for its whole day; only expiry closes it.
                return (renderer.Agenda(agenda), false);

            case NotificationKind.DaySummary:
                var summary = await views.BuildSummaryAsync(
                    row.LocalDate!.Value, profile, focusedLessonId: null, expanded: false, nowUtc, ct);
                return (renderer.Summary(summary), false);

            default:
                return null;
        }
    }

    private async Task RetractAsync(NotificationDispatch row, DateTimeOffset now, CancellationToken ct)
    {
        row.Retract(now);
        dispatches.Update(row);
        await uow.SaveChangesAsync(ct);
    }
}
