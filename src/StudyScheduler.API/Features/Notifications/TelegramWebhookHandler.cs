using System.Globalization;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Processes a single Telegram <see cref="Telegram.Bot.Types.Update"/>: re-enables a tutor's bot
/// reachability on any interaction (a <c>/start</c> or a button tap resumes notifications we'd
/// disabled after a 403), and drives the evening summary's in-message state machine — the only screen
/// a tutor navigates without leaving Telegram.
/// The tapped message names itself: its <see cref="NotificationDispatch"/> row says WHAT that message
/// is (a reminder about one lesson, a digest about one local date), so every tap is answered by
/// re-rendering that same message from current data through <see cref="NotificationRenderer"/> and
/// re-syncing the row, leaving nothing for reconciliation to repair.
/// The mutation itself is not this handler's business: it goes through the very same
/// <see cref="LessonService"/> seam the app's <c>PATCH /lessons/{id}</c> uses, so a button tap and a
/// hand edit are the same statement about the same lesson — the <c>IsCustomized</c> latch included.
/// The webhook is anonymous — Telegram calls it, not a tutor — so the update itself carries the
/// identity: the user it came from becomes the scope's tenant before any row is read or written, and
/// that tenant is what scopes every lookup below. A payload can therefore only ever reach the sender's
/// own data.
/// The handler is deliberately resilient — a malformed update is answered (or ignored) but never
/// throws, so the endpoint can always ack 200 and Telegram won't retry-storm.
/// </summary>
public sealed class TelegramWebhookHandler(
    LessonService lessons,
    ITutorProfileRepository profiles,
    INotificationDispatchRepository dispatches,
    NotificationViewBuilder views,
    NotificationRenderer renderer,
    INotificationSender sender,
    IUnitOfWork uow,
    ITutorScope tenant,
    TimeProvider clock,
    ILogger<TelegramWebhookHandler> logger)
{
    public async Task HandleAsync(Telegram.Bot.Types.Update update, CancellationToken ct = default)
    {
        try
        {
            // Any interaction from a tutor whose bot we'd flagged unreachable resumes their notifications.
            var userId = update.CallbackQuery?.From.Id ?? update.Message?.From?.Id;
            if (userId is { } id)
            {
                // The sender of the update is the tenant of everything below — including the callback
                // branch, which acts on the very same user.
                tenant.SetForBackground(id);
                await EnsureReachableAsync(id, ct);
            }

            if (update.CallbackQuery is { } cq)
                await HandleCallbackAsync(cq, ct);

            // Non-callback, non-message updates (edits, channel posts, …) carry no action for us.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The endpoint must ack 200 whatever happened, or Telegram redelivers the same update in a
            // tightening loop. A failed tap is simply an unanswered button.
            logger.LogError(ex, "Telegram update {UpdateId} failed", update.Id);
        }
    }

    /// <summary>Flips a previously-disabled bot back on so the poller starts targeting the tutor again.</summary>
    private async Task EnsureReachableAsync(long userId, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        if (profile is null || profile.BotReachable)
            return;

        profile.MarkBotReachable();
        profiles.Update(profile);
        await uow.SaveChangesAsync(ct);
        logger.LogInformation("Re-enabled bot reachability for tutor {TutorId} after an interaction", userId);
    }

    private async Task HandleCallbackAsync(Telegram.Bot.Types.CallbackQuery cq, CancellationToken ct)
    {
        if (!TryParse(cq.Data, out var command))
        {
            // Nothing in our dictionary — and nothing the spec gives us words for. Answer to stop the
            // button's spinner and drop it.
            logger.LogWarning("Malformed callback data '{Data}' from tutor {TutorId}", cq.Data, cq.From.Id);
            await sender.AnswerCallbackAsync(cq.Id, null, ct);
            return;
        }

        var profile = await profiles.GetAsync(ct);
        var lang = profile?.LanguageCode ?? AppLanguage.Uk;
        var now = clock.GetUtcNow();

        // The tapped message tells us WHICH message to re-render. A pre-redesign message has no row:
        // its mutation still applies, it just cannot be repainted.
        var dispatch = cq.Message is { } tapped
            ? await dispatches.GetByMessageAsync(tapped.Chat.Id, tapped.MessageId, track: true, ct)
            : null;

        // s: / b: / m: mutate nothing — they only change the arguments the same message is rendered
        // from. Neither the focus nor the expansion is persisted: a later reconciliation legitimately
        // repaints the collapsed list.
        if (command.Action is CallbackAction.Focus or CallbackAction.Back or CallbackAction.Expand)
        {
            await sender.AnswerCallbackAsync(cq.Id, null, ct);
            await RerenderAsync(
                dispatch, cq, profile,
                focusedLessonId: command.Action == CallbackAction.Focus ? command.LessonId : null,
                expanded: command.Action == CallbackAction.Expand,
                now, ct);
            return;
        }

        var result = command.Action == CallbackAction.MarkAll
            ? await MarkDayAsync(command.Date, profile, now, ct)
            : await MarkOneAsync(command, cq.From.Id, ct);

        await sender.AnswerCallbackAsync(cq.Id, renderer.Toast(lang, result.Toast, result.Count), ct);

        // A refused or failed mutation leaves the message exactly as it was (spec §7): the tutor's next
        // tap must still address the state they can see.
        if (result.Succeeded)
            await RerenderAsync(dispatch, cq, profile, focusedLessonId: null, expanded: false, now, ct);
    }

    /// <summary>
    /// Applies one <c>c:</c>/<c>p:</c>/<c>x:</c> tap through the lesson façade and picks the toast that
    /// describes what happened.
    /// </summary>
    private async Task<CallbackResult> MarkOneAsync(CallbackCommand command, long tutorId, CancellationToken ct)
    {
        // The tenant is the sender, so ownership is enforced by the read itself: another tutor's lesson
        // is simply not in this scope's rows.
        var lesson = await lessons.GetAsync(command.LessonId, ct);
        if (lesson is null)
        {
            logger.LogWarning(
                "Callback for lesson {LessonId} not found for tutor {TutorId}", command.LessonId, tutorId);
            return new CallbackResult(NotificationToast.NoLongerExists, 0, Succeeded: false);
        }

        // A cancel may drop a Scheduled lesson at any time — a no-show is a legitimate cancel long after
        // the scheduled start. But a lesson already recorded Completed must not be silently undone. The
        // domain refuses that transition too; this pre-check exists for the toast, which is the only way
        // the tutor learns WHY.
        if (command.Action == CallbackAction.Cancel && lesson.Status == LessonStatus.Completed)
        {
            logger.LogInformation(
                "Cancel for lesson {LessonId} ignored: already recorded Completed (tutor {TutorId})",
                command.LessonId, tutorId);
            return new CallbackResult(NotificationToast.AlreadyChanged, 0, Succeeded: false);
        }

        var outcome = await lessons.UpdateAsync(command.LessonId, Patch(command.Action), ct);
        if (outcome is not LessonPatchOutcome.Ok)
        {
            // A null outcome means the row vanished between the read above and the patch.
            logger.LogWarning(
                "Callback mutation of lesson {LessonId} for tutor {TutorId} failed with {Outcome}",
                command.LessonId, tutorId, outcome?.GetType().Name ?? "NotFound");
            return new CallbackResult(
                outcome is null ? NotificationToast.NoLongerExists : NotificationToast.CouldNotSave,
                0, Succeeded: false);
        }

        var toast = command.Action switch
        {
            CallbackAction.Complete => NotificationToast.LessonMarked,
            CallbackAction.CompletePaid => NotificationToast.MarkedAsPaid,
            _ => NotificationToast.LessonCancelled,
        };
        return new CallbackResult(toast, 0, Succeeded: true);
    }

    /// <summary>
    /// «✅ Усі проведено» — every still-Scheduled lesson of that local day, each through its own
    /// <see cref="LessonService.UpdateAsync"/> call, so a single stubborn row cannot take the rest with it.
    /// </summary>
    private async Task<CallbackResult> MarkDayAsync(
        DateOnly localDate, TutorProfile? profile, DateTimeOffset now, CancellationToken ct)
    {
        // The day's composition is read in the tutor's own zone, which only the profile knows.
        if (profile is null)
            return new CallbackResult(NotificationToast.CouldNotSave, 0, Succeeded: false);

        var view = await views.BuildSummaryAsync(localDate, profile, null, expanded: true, now, ct);

        var marked = 0;
        foreach (var line in view.Lines.Where(l => l.Status == LessonStatus.Scheduled))
        {
            var outcome = await lessons.UpdateAsync(line.LessonId, Patch(CallbackAction.Complete), ct);
            if (outcome is LessonPatchOutcome.Ok)
                marked++;
            else
                logger.LogWarning(
                    "Bulk mark of lesson {LessonId} on {LocalDate} failed with {Outcome}",
                    line.LessonId, localDate, outcome?.GetType().Name ?? "NotFound");
        }

        return new CallbackResult(NotificationToast.AllMarked, marked, Succeeded: true);
    }

    /// <summary>
    /// Repaints the tapped message from current data and records what is now on screen. An unchanged
    /// content hash costs no Telegram call. Once the summary lands in its closed form there is nothing
    /// left to keep alive, so the row is retracted as well.
    /// </summary>
    private async Task RerenderAsync(
        NotificationDispatch? dispatch,
        Telegram.Bot.Types.CallbackQuery cq,
        TutorProfile? profile,
        Guid? focusedLessonId,
        bool expanded,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (dispatch is null || profile is null || cq.Message is not { } message)
            return;

        RenderedMessage rendered;
        var dayClosed = false;

        switch (dispatch.Kind)
        {
            case NotificationKind.DaySummary when dispatch.LocalDate is { } localDate:
            {
                var view = await views.BuildSummaryAsync(localDate, profile, focusedLessonId, expanded, now, ct);

                // The focused lesson can have left the day between the render and the tap (rescheduled,
                // deleted); fall back to the list rather than render a lesson that isn't there.
                if (view.FocusedLessonId is { } focused && view.Lines.All(l => l.LessonId != focused))
                    view = view with { FocusedLessonId = null };

                dayClosed = view.FocusedLessonId is null
                    && view.Lines.All(l => l.Status != LessonStatus.Scheduled);
                rendered = renderer.Summary(view);
                break;
            }

            case NotificationKind.Reminder when dispatch.LessonId is { } lessonId:
            {
                var view = await views.BuildReminderAsync(lessonId, profile, dispatch.Snapshot, now, ct);
                if (view is null)
                    return;

                rendered = renderer.Reminder(view);
                break;
            }

            default:
                // A morning agenda carries url buttons only — no callback can land on one.
                return;
        }

        if (rendered.ContentHash != dispatch.ContentHash)
        {
            var edit = await sender.EditMessageAsync(
                message.Chat.Id, message.MessageId, rendered.Text, rendered.Rows, ct);
            if (edit != TelegramSendResult.Delivered)
            {
                // The row keeps describing what is actually on screen, so the next reconciliation pass
                // sees the drift and retries it.
                logger.LogWarning(
                    "Re-render of message {MessageId} for tutor {TutorId} came back {Result}",
                    message.MessageId, cq.From.Id, edit);
                return;
            }

            dispatch.Resync(rendered.Snapshot, rendered.ContentHash, now);
        }

        if (dayClosed)
            dispatch.Retract(now);

        dispatches.Update(dispatch);
        await uow.SaveChangesAsync(ct);
    }

    /// <summary>The lesson patch a mark action maps to — the same body the app's PATCH would send.</summary>
    private static UpdateLessonRequest Patch(CallbackAction action) => action switch
    {
        CallbackAction.Complete => new UpdateLessonRequest(null, null, LessonStatus.Completed, null, null, null, null),
        CallbackAction.CompletePaid => new UpdateLessonRequest(null, null, LessonStatus.Completed, null, true, null, null),
        _ => new UpdateLessonRequest(null, null, LessonStatus.Cancelled, null, null, null, null),
    };

    /// <summary>
    /// The whole callback dictionary: <c>c:{guid32}</c> completed · <c>p:{guid32}</c> completed + paid ·
    /// <c>x:{guid32}</c> cancelled · <c>s:{guid32}</c> summary step 2 · <c>b:</c> back ·
    /// <c>a:{yyyy-MM-dd}</c> mark the rest of the day · <c>m:{yyyy-MM-dd}</c> expand past the first six.
    /// One verb letter, a colon and the argument — every payload well under Telegram's 64-byte limit.
    /// Anything else is rejected.
    /// </summary>
    private static bool TryParse(string? data, out CallbackCommand command)
    {
        command = default;
        if (data is null || data.Length < 2 || data[1] != ':')
            return false;

        var argument = data[2..];
        switch (data[0])
        {
            case 'c' or 'p' or 'x' or 's':
                if (!Guid.TryParseExact(argument, "N", out var lessonId))
                    return false;

                command = new CallbackCommand(data[0] switch
                {
                    'c' => CallbackAction.Complete,
                    'p' => CallbackAction.CompletePaid,
                    'x' => CallbackAction.Cancel,
                    _ => CallbackAction.Focus,
                }, lessonId, default);
                return true;

            case 'b':
                if (argument.Length != 0)
                    return false;

                command = new CallbackCommand(CallbackAction.Back, default, default);
                return true;

            case 'a' or 'm':
                if (!DateOnly.TryParseExact(
                        argument, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return false;

                command = new CallbackCommand(
                    data[0] == 'a' ? CallbackAction.MarkAll : CallbackAction.Expand, default, date);
                return true;

            default:
                return false;
        }
    }

    private enum CallbackAction { Complete, CompletePaid, Cancel, Focus, Back, MarkAll, Expand }

    private readonly record struct CallbackCommand(CallbackAction Action, Guid LessonId, DateOnly Date);

    /// <summary>
    /// What a tap did and what to tell the tutor. <paramref name="Succeeded"/> is what gates the
    /// re-render: a refusal must leave the message alone.
    /// </summary>
    private readonly record struct CallbackResult(NotificationToast Toast, int Count, bool Succeeded);
}
