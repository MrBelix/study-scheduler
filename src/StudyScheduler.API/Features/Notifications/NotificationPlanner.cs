using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Decides which notifications a tutor's schedule owes at a given instant. Pure and I/O-free: it
/// reads the tutor's opt-ins plus each entry's already-sent flags (the durable per-lesson dedup)
/// and returns what is due now. A cancelled entry is never notified.
/// </summary>
public sealed class NotificationPlanner
{
    public IReadOnlyList<DueNotification> Plan(
        TutorProfile profile,
        IReadOnlyList<ScheduleEntry> schedule,
        DateTimeOffset nowUtc,
        int followUpLookbackMinutes)
    {
        var due = new List<DueNotification>();
        foreach (var entry in schedule)
        {
            if (entry.Status == LessonStatus.Cancelled)
                continue;

            if (profile.RemindMinutes is { } remind
                && !entry.Notifications.IsReminderSent
                && entry.StartUtc.AddMinutes(-remind) <= nowUtc
                && nowUtc < entry.StartUtc)
                due.Add(new DueNotification(NotificationKind.Reminder, entry));

            if (profile.NotifyAfterLesson
                && !entry.Notifications.IsFollowUpSent
                && entry.EndUtc <= nowUtc
                && entry.EndUtc > nowUtc.AddMinutes(-followUpLookbackMinutes))
                due.Add(new DueNotification(NotificationKind.FollowUp, entry));
        }

        return due;
    }
}
