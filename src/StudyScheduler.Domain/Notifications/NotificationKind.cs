namespace StudyScheduler.Domain.Notifications;

/// <summary>
/// The three bot notifications the schedule produces: the pre-lesson reminder, the morning agenda
/// for a whole day and the evening summary that collects that day's unmarked lessons. Lives in the
/// domain rather than in the feature because <see cref="NotificationDispatch"/> persists it.
/// </summary>
public enum NotificationKind
{
    Reminder,
    DayAgenda,
    DaySummary,
}
