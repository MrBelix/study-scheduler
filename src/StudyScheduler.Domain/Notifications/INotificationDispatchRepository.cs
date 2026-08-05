namespace StudyScheduler.Domain.Notifications;

/// <summary>
/// Persistence contract for <see cref="NotificationDispatch"/>. Lives in the domain so the API
/// depends on the abstraction; infrastructure (EF Core) provides the implementation. Every method
/// here reads the CURRENT TENANT's rows and nothing else — ownership is enforced by persistence
/// rather than passed in, so a cross-tenant id reads exactly like a missing one. The single
/// exception says so in its own name.
/// </summary>
public interface INotificationDispatchRepository
{
    /// <summary>
    /// Every <see cref="DispatchState.Delivered"/> row of this tutor — the reconciliation queue.
    /// Deliberately NOT bounded by <see cref="NotificationDispatch.ExpiresAtUtc"/>: an expired row
    /// still owes one final pass (its closing form), and retracting it there is what keeps the queue
    /// bounded.
    /// </summary>
    Task<IReadOnlyList<NotificationDispatch>> GetLiveAsync(bool track = false, CancellationToken ct = default);

    /// <summary>The live reminder for a lesson, or null when none is armed.</summary>
    Task<NotificationDispatch?> GetLiveForLessonAsync(Guid lessonId, bool track = false, CancellationToken ct = default);

    /// <summary>The live digest of the given kind for a local date, or null when none went out.</summary>
    Task<NotificationDispatch?> GetLiveForDayAsync(
        NotificationKind kind, DateOnly localDate, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// The dispatch occupying a specific Telegram message — how a tapped button finds what it
    /// belongs to. Any state: a retracted message can still be tapped.
    /// </summary>
    Task<NotificationDispatch?> GetByMessageAsync(long chatId, int messageId, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// EVERY tutor that currently owns at least one live dispatch. Read-only, and one of the three
    /// deliberately un-scoped queries in the app (<c>IgnoreQueryFilters</c>): its only caller is the
    /// reconciliation pass, which runs without a user context and must see all tenants — hence the
    /// name it has to be called by.
    /// </summary>
    Task<IReadOnlyList<long>> GetTutorsWithLiveDispatchesAcrossAllTutorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Stages the dispatch for insertion. Ownership is not set here: the scope's tutor is stamped
    /// onto it when the unit of work commits.
    /// </summary>
    void Add(NotificationDispatch dispatch);

    void Update(NotificationDispatch dispatch);
}
