using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>One notification the poller should send for a schedule slot.</summary>
public sealed record PlannedNotification(LessonNotificationKind Kind, string SlotKey, ScheduleSlot Slot);

/// <summary>
/// Pure planning half of the poller: which slots of a tutor's schedule are due a reminder
/// (starting within the profile's lead window) or a follow-up (ended within the lookback
/// window and still <see cref="LessonStatus.Scheduled"/>). Dedup against already-sent
/// notifications happens in the caller.
/// </summary>
public static class NotificationPlanner
{
    public static List<PlannedNotification> Plan(
        TutorProfile profile,
        IReadOnlyList<ScheduleSlot> slots,
        DateTimeOffset nowUtc,
        int followUpLookbackMinutes)
    {
        var planned = new List<PlannedNotification>();
        foreach (var slot in slots)
        {
            // Completed needs no follow-up; cancelled slots need neither.
            if (slot.Status != LessonStatus.Scheduled)
                continue;

            var slotKey = LessonNotification.ForLessonSlot(slot.Id, slot.SeriesId, slot.OccurrenceDate);

            if (profile.RemindMinutes is { } remindMinutes
                && slot.StartUtc > nowUtc
                && slot.StartUtc <= nowUtc.AddMinutes(remindMinutes))
                planned.Add(new PlannedNotification(LessonNotificationKind.Reminder, slotKey, slot));

            if (profile.NotifyAfterLesson
                && slot.EndUtc <= nowUtc
                && slot.EndUtc > nowUtc.AddMinutes(-followUpLookbackMinutes))
                planned.Add(new PlannedNotification(LessonNotificationKind.FollowUp, slotKey, slot));
        }

        return planned;
    }
}
