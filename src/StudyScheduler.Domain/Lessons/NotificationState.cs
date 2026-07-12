namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Which bot notifications have already been sent for a lesson, and when. A value object owned by
/// <see cref="Lesson"/> — the durable per-lesson dedup for the notification poller (a set timestamp
/// means "already sent"). Immutable: mutators return a new instance. Kind-agnostic — the notification
/// KIND enum lives in the Notifications feature; the domain just holds two facts.
/// </summary>
public sealed record NotificationState(DateTimeOffset? ReminderSentAtUtc, DateTimeOffset? FollowUpSentAtUtc)
{
    /// <summary>Nothing sent yet — the state of a freshly created lesson.</summary>
    public static readonly NotificationState None = new(null, null);

    public bool IsReminderSent => ReminderSentAtUtc is not null;
    public bool IsFollowUpSent => FollowUpSentAtUtc is not null;

    public NotificationState WithReminderSent(DateTimeOffset sentAtUtc) => this with { ReminderSentAtUtc = sentAtUtc };
    public NotificationState WithFollowUpSent(DateTimeOffset sentAtUtc) => this with { FollowUpSentAtUtc = sentAtUtc };
}
