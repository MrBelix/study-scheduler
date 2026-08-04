using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// One tick of notification delivery: for every notifiable tutor, plans the due reminders/follow-ups
/// off their schedule, re-reads the lesson as the tracked row it is, sends the message and — only on a
/// settled outcome — records the send and commits the flag. Every lesson already exists as a row when
/// it is planned (a series generates its lessons months ahead), so the buttons carry an id that is
/// durable before anything goes out. Each send is committed on its own, so one blocked chat never
/// blocks another; a transient failure leaves the lesson unmarked to be retried against the same id
/// next tick. If a send comes back <see cref="TelegramSendResult.Unreachable"/> (a 403) the tutor's
/// bot flag is flipped off and the rest of their due notifications are skipped for this tick.
/// Per-tutor failures are isolated.
/// The tick has no tenant of its own: it reads the notifiable profiles across all tutors, then makes
/// each tutor the scope's tenant before touching anything of theirs — which is what scopes every
/// lesson and student read below to that one tutor.
/// </summary>
public sealed class NotificationRunner(
    ITutorProfileRepository profiles,
    ILessonRepository lessons,
    IStudentRepository students,
    INotificationSender sender,
    NotificationPlanner planner,
    NotificationText text,
    IUnitOfWork uow,
    ITutorScope tenant,
    TimeProvider clock,
    IOptions<NotificationsOptions> options,
    ILogger<NotificationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var lookback = options.Value.FollowUpLookbackMinutes;

        foreach (var profile in await profiles.GetNotifiableAcrossAllTutorsAsync(ct))
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
        // From here on the scope IS this tutor: every lesson and student read below is theirs.
        tenant.SetForBackground(profile.TelegramUserId);

        var from = now.AddMinutes(-lookback);
        var to = now.AddMinutes(profile.RemindMinutes ?? 0);

        var schedule = await lessons.GetInRangeAsync(from, to, ct: ct);
        var due = planner.Plan(profile, schedule, now, lookback);
        if (due.Count == 0)
            return;

        var studentIds = due.Select(d => d.Lesson.StudentId).Distinct().ToList();
        var names = (await students.GetByIdsAsync(studentIds, ct))
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
    /// Loads the lesson as a tracked row, sends its message and settles the outcome. Returns
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

        // 1. The planner read an untracked snapshot; the authoritative row is the tracked one.
        var lesson = await lessons.GetByIdAsync(d.Lesson.Id, track: true, ct);
        if (lesson is null)
            return true;

        // 2. Concurrency guard: if the notification was already sent since the snapshot, do nothing.
        if (d.Kind == NotificationKind.Reminder && lesson.Notifications.IsReminderSent)
            return true;
        if (d.Kind == NotificationKind.FollowUp && lesson.Notifications.IsFollowUpSent)
            return true;

        // 3. Build the message off the persisted lesson id.
        var name = names.GetValueOrDefault(lesson.StudentId, "");
        string body;
        IReadOnlyList<NotificationButton> buttons;
        if (d.Kind == NotificationKind.Reminder)
        {
            var localStart = TimeZoneInfo.ConvertTime(lesson.StartUtc, profile.TimeZone);
            body = text.Reminder(lang, name, localStart);
            buttons = text.ReminderButtons(lang, lesson.Id);
        }
        else
        {
            body = text.FollowUp(lang, name);
            buttons = text.FollowUpButtons(lang, lesson.Id);
        }

        // 4. Send.
        var result = await sender.SendAsync(tutorId, body, buttons, ct);

        // 5. Settle by outcome. The lesson is persisted either way — only the sent flag is at stake.
        switch (result)
        {
            case TelegramSendResult.TransientFailure:
                // Mark nothing: the lesson is retried against the SAME id next tick.
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
