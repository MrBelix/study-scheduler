using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// One poller tick: for every tutor with notifications enabled, expands the schedule around
/// "now" (virtual slots included — no lesson rows needed), plans due reminders/follow-ups,
/// filters out already-sent ones via the dedup log, sends the rest and records them. A send
/// failure is not recorded, so the next tick retries it.
/// </summary>
public sealed class LessonNotifier(
    ITutorProfileRepository profiles,
    IStudentRepository students,
    ILessonNotificationRepository sentLog,
    IUnitOfWork uow,
    LessonMaterializer materializer,
    ITelegramBotClient bot,
    IOptions<NotificationsOptions> options,
    TimeProvider clock,
    ILogger<LessonNotifier> logger)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var nowUtc = clock.GetUtcNow();
        var lookbackMinutes = options.Value.FollowUpLookbackMinutes;

        foreach (var profile in await profiles.GetWithNotificationsEnabledAsync(ct))
        {
            try
            {
                await NotifyTutorAsync(profile, nowUtc, lookbackMinutes, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One tutor's failure must not starve the rest of the loop — and its staged
                // entities must not poison the remaining tutors' saves in this shared scope.
                uow.DiscardChanges();
                logger.LogError(
                    exception, "Notification pass failed for tutor {TutorTelegramId}", profile.TelegramUserId);
            }
        }
    }

    private async Task NotifyTutorAsync(
        TutorProfile profile, DateTimeOffset nowUtc, int lookbackMinutes, CancellationToken ct)
    {
        // Window covers both directions; +1 min because the range end is exclusive.
        var fromUtc = nowUtc.AddMinutes(-lookbackMinutes);
        var toUtc = nowUtc.AddMinutes((profile.RemindMinutes ?? 0) + 1);

        var slots = await materializer.GetScheduleAsync(profile.TelegramUserId, fromUtc, toUtc, ct: ct);
        var planned = NotificationPlanner.Plan(profile, slots, nowUtc, lookbackMinutes);
        if (planned.Count == 0)
            return;

        var fresh = new List<PlannedNotification>();
        foreach (var kindGroup in planned.GroupBy(p => p.Kind))
        {
            var sent = await sentLog.GetSentSlotKeysAsync(
                profile.TelegramUserId, kindGroup.Key, kindGroup.Select(p => p.SlotKey).ToList(), ct);
            fresh.AddRange(kindGroup.Where(p => !sent.Contains(p.SlotKey)));
        }

        if (fresh.Count == 0)
            return;

        var names = await ResolveStudentNamesAsync(profile.TelegramUserId, fresh, ct);

        foreach (var notification in fresh)
        {
            var slot = notification.Slot;
            var studentName = names.GetValueOrDefault(slot.StudentId, "?");
            var timeRange = FormatTimeRange(slot, profile.TimeZone);

            var delivered = notification.Kind == LessonNotificationKind.Reminder
                ? await bot.SendMessageAsync(
                    profile.TelegramUserId,
                    NotificationMessages.Reminder(
                        profile.LanguageCode, studentName, timeRange,
                        (int)Math.Round((slot.StartUtc - nowUtc).TotalMinutes)),
                    ct: ct)
                : await bot.SendMessageAsync(
                    profile.TelegramUserId,
                    NotificationMessages.FollowUpPrompt(profile.LanguageCode, studentName, timeRange),
                    NotificationMessages.FollowUpButtons(
                        profile.LanguageCode, slot.Id, slot.SeriesId, slot.OccurrenceDate),
                    ct);

            if (!delivered)
                continue;

            logger.LogInformation(
                "Sent {Kind} for slot {SlotKey} to tutor {TutorTelegramId}",
                notification.Kind, notification.SlotKey, profile.TelegramUserId);
            // Commit per send, not per tick: a crash between the Telegram send and the record
            // must lose at most one dedup row (one duplicate message on the next tick).
            sentLog.Add(LessonNotification.Create(
                profile.TelegramUserId, notification.Kind, notification.SlotKey, nowUtc));
            await uow.SaveChangesAsync(ct);
        }
    }

    private async Task<Dictionary<Guid, string>> ResolveStudentNamesAsync(
        long tutorTelegramId, List<PlannedNotification> notifications, CancellationToken ct)
    {
        var studentIds = notifications.Select(n => n.Slot.StudentId).Distinct().ToList();
        return (await students.GetByIdsAsync(tutorTelegramId, studentIds, ct))
            .ToDictionary(s => s.Id, s => s.Name);
    }

    internal static string FormatTimeRange(ScheduleSlot slot, TimeZoneInfo timeZone)
    {
        var start = TimeZoneInfo.ConvertTime(slot.StartUtc, timeZone);
        var end = TimeZoneInfo.ConvertTime(slot.EndUtc, timeZone);
        return $"{start:HH\\:mm}–{end:HH\\:mm}";
    }
}
