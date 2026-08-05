namespace StudyScheduler.Domain.Notifications;

/// <summary>
/// Whether a dispatch is still the live message for its target. <see cref="Retracted"/> is
/// permanent and deliberately does NOT block a new dispatch for the same target — that is what
/// re-arms a reminder after the lesson is rescheduled.
/// </summary>
public enum DispatchState
{
    Delivered,
    Retracted,
}
