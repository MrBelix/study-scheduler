using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Decides which notifications a tutor's schedule owes at a given instant. Pure and I/O-free: it
/// reads the tutor's opt-ins plus each lesson's already-sent flags (the durable per-lesson dedup)
/// and returns what is due now. A cancelled lesson is never notified.
/// </summary>
public sealed class NotificationPlanner
{
    public IReadOnlyList<DueNotification> Plan(
        TutorProfile profile,
        IReadOnlyList<Lesson> schedule,
        DateTimeOffset nowUtc,
        int followUpLookbackMinutes)
    {
        var due = new List<DueNotification>();
        foreach (var lesson in schedule)
        {
            if (lesson.Status == LessonStatus.Cancelled)
                continue;

            if (profile.RemindMinutes is { } remind
                && !lesson.Notifications.IsReminderSent
                && lesson.StartUtc.AddMinutes(-remind) <= nowUtc
                && nowUtc < lesson.StartUtc)
                due.Add(new DueNotification(NotificationKind.Reminder, lesson));

            if (profile.NotifyAfterLesson
                && !lesson.Notifications.IsFollowUpSent
                && lesson.EndUtc <= nowUtc
                && lesson.EndUtc > nowUtc.AddMinutes(-followUpLookbackMinutes))
                due.Add(new DueNotification(NotificationKind.FollowUp, lesson));
        }

        return due;
    }
}
