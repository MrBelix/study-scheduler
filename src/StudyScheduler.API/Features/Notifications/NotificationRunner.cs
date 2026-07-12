using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// One tick of notification delivery: for every notifiable tutor, plans the due reminders/follow-ups
/// off their merged schedule, obtains a persisted physical lesson (materializing and saving a virtual
/// slot up-front so its id is durable before any message goes out), sends the message and — only on a
/// settled outcome — records the send and commits the flag. Each send is committed on its own, so one
/// blocked chat never blocks another; a transient failure leaves the lesson persisted-but-unmarked to
/// be retried against the same id next tick. If a send comes back <see cref="TelegramSendResult.Unreachable"/>
/// (a 403) the tutor's bot flag is flipped off and the rest of their due notifications are skipped for
/// this tick. Per-tutor failures are isolated.
/// </summary>
public sealed class NotificationRunner(
    ITutorProfileRepository profiles,
    ScheduleReader schedule,
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo,
    LessonMaterializer materializer,
    IStudentRepository students,
    INotificationSender sender,
    NotificationPlanner planner,
    NotificationText text,
    IUnitOfWork uow,
    TimeProvider clock,
    IOptions<NotificationsOptions> options,
    ILogger<NotificationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var lookback = options.Value.FollowUpLookbackMinutes;

        foreach (var profile in await profiles.GetNotifiableAsync(ct))
        {
            try
            {
                await RunForTutorAsync(profile, now, lookback, ct);
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

    private async Task RunForTutorAsync(TutorProfile profile, DateTimeOffset now, int lookback, CancellationToken ct)
    {
        var tutorId = profile.TelegramUserId;
        var from = now.AddMinutes(-lookback);
        var to = now.AddMinutes(profile.RemindMinutes ?? 0);

        var entries = await schedule.GetScheduleAsync(tutorId, from, to, ct: ct);
        var due = planner.Plan(profile, entries, now, lookback);
        if (due.Count == 0)
            return;

        var studentIds = due.Select(d => d.Entry.StudentId).Distinct().ToList();
        var names = (await students.GetByIdsAsync(tutorId, studentIds, ct))
            .ToDictionary(s => s.Id, s => s.Name);
        var lang = profile.LanguageCode ?? AppLanguage.Uk;

        foreach (var d in due)
        {
            // A 403 disables the tutor's bot; there is no point sending the rest of their due
            // notifications to an unreachable chat this tick.
            if (!await SendOneAsync(profile, d, names, lang, now, ct))
                break;
        }
    }

    /// <summary>
    /// Materializes/loads a persisted lesson, sends its message and settles the outcome. Returns
    /// <c>true</c> if the tutor's bot is still reachable (keep processing their queue) and
    /// <c>false</c> when a 403 flipped the reachability flag off (stop processing this tutor).
    /// </summary>
    private async Task<bool> SendOneAsync(
        TutorProfile profile,
        DueNotification d,
        IReadOnlyDictionary<Guid, string> names,
        AppLanguage lang,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var tutorId = profile.TelegramUserId;
        var entry = d.Entry;

        // 1. Obtain a persisted lesson. A virtual slot is materialized and saved up-front so the
        //    follow-up buttons carry a lesson id that is already durable in the DB; a physical lesson
        //    is loaded tracked (already persisted, so no pre-save).
        Lesson lesson;
        if (entry.IsVirtual)
        {
            var series = await seriesRepo.GetByIdAsync(entry.SeriesId!.Value, tutorId, ct: ct);
            if (series is null)
            {
                logger.LogWarning(
                    "Series {SeriesId} behind virtual slot {OccurrenceDate} not found; skipping {Kind}",
                    entry.SeriesId, entry.OccurrenceDate, d.Kind);
                return true;
            }

            var occ = new LessonOccurrence(entry.OccurrenceDate!.Value, entry.StartUtc, entry.EndUtc);
            lesson = await materializer.MaterializeSlotAsync(series, occ, ct);

            lessons.Add(lesson);
            try
            {
                await uow.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (SqlErrors.IsDuplicateKey(ex))
            {
                // A concurrent materialization (an occurrence PATCH or the webhook) won this slot
                // first. Discard the doomed insert and adopt the row that actually landed so the
                // send targets a real, persisted id.
                uow.DiscardChanges();
                var persisted = await lessons.GetBySeriesOccurrenceAsync(
                    entry.SeriesId!.Value, entry.OccurrenceDate!.Value, tutorId, track: true, ct);
                if (persisted is null)
                {
                    logger.LogWarning(
                        "Slot {OccurrenceDate} of series {SeriesId} vanished after a materialization race; skipping {Kind}",
                        entry.OccurrenceDate, entry.SeriesId, d.Kind);
                    return true;
                }

                lesson = persisted;
            }
        }
        else
        {
            var existing = await lessons.GetByIdAsync(entry.Id!.Value, tutorId, track: true, ct);
            if (existing is null)
                return true;

            lesson = existing;
        }

        // 2. Concurrency guard: the planner read an untracked snapshot; the now-authoritative lesson
        //    is the tracked/persisted row. If it was already sent since, do nothing.
        if (d.Kind == NotificationKind.Reminder && lesson.Notifications.IsReminderSent)
            return true;
        if (d.Kind == NotificationKind.FollowUp && lesson.Notifications.IsFollowUpSent)
            return true;

        // 3. Build the message off the now-persisted lesson id.
        var name = names.GetValueOrDefault(lesson.StudentId, "");
        string body;
        IReadOnlyList<NotificationButton> buttons;
        if (d.Kind == NotificationKind.Reminder)
        {
            var localStart = TimeZoneInfo.ConvertTime(lesson.StartUtc, profile.TimeZone);
            body = text.Reminder(lang, name, localStart);
            buttons = [];
        }
        else
        {
            body = text.FollowUp(lang, name);
            buttons = text.FollowUpButtons(lang, lesson.Id);
        }

        // 4. Send.
        var result = await sender.SendAsync(tutorId, body, buttons, ct);

        // 5. Settle by outcome. Ordering is persist-before-send throughout, so a materialized slot
        //    is already durable regardless of the send result.
        switch (result)
        {
            case TelegramSendResult.TransientFailure:
                // Mark nothing: the lesson stays persisted and is retried against the SAME id next tick.
                logger.LogWarning(
                    "Transient failure sending {Kind} for lesson {LessonId} to tutor {TutorId}; will retry against the same id",
                    d.Kind, lesson.Id, tutorId);
                return true;

            case TelegramSendResult.Unreachable:
                // A 403: the tutor never started or blocked the bot. Flip the reachability flag off
                // so the poller skips this tutor, and leave the lesson UNMARKED so it can still fire
                // once the bot is re-enabled while the notification is in-window. Signal the caller
                // to stop draining this tutor's queue.
                profile.MarkBotUnreachable();
                profiles.Update(profile);
                await uow.SaveChangesAsync(ct);
                logger.LogWarning(
                    "Tutor {TutorId} chat unreachable (403) sending {Kind} for lesson {LessonId}; disabling bot and skipping remaining notifications this tick",
                    tutorId, d.Kind, lesson.Id);
                return false;

            case TelegramSendResult.PermanentFailure:
                // A 400 bad request: our message will never be accepted, so mark it sent to stop it
                // looping and surface the defect as an error.
                logger.LogError(
                    "Permanent failure (400) sending {Kind} for lesson {LessonId} to tutor {TutorId}; marking sent to avoid a retry loop",
                    d.Kind, lesson.Id, tutorId);
                break;

            case TelegramSendResult.Delivered:
                break;
        }

        // Delivered or PermanentFailure: record the send flag and commit just that mutation.
        if (d.Kind == NotificationKind.Reminder)
            lesson.MarkReminderSent(now);
        else
            lesson.MarkFollowUpSent(now);

        await uow.SaveChangesAsync(ct);

        logger.LogInformation(
            "Sent {Kind} for lesson {LessonId} to tutor {TutorId} ({Result})",
            d.Kind, lesson.Id, tutorId, result);
        return true;
    }
}
