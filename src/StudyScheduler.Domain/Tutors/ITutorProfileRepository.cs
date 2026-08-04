namespace StudyScheduler.Domain.Tutors;

/// <summary>Persistence contract for <see cref="TutorProfile"/> (one row per tutor).</summary>
public interface ITutorProfileRepository
{
    /// <summary>
    /// The current tenant's profile, or null until it is first saved. A profile is KEYED by the tutor
    /// id, so the tenancy filter sits on its key: there is exactly one profile a scope can reach.
    /// </summary>
    Task<TutorProfile?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// EVERY tutor's profile that opts into at least one bot notification (a reminder lead time set
    /// or the after-lesson follow-up enabled) AND whose bot chat is currently reachable. Read-only,
    /// and the one deliberately un-scoped query over profiles: its only caller is the notification
    /// poller, which runs without a user context and must see all tenants — hence the name it has to
    /// be called by.
    /// </summary>
    Task<IReadOnlyList<TutorProfile>> GetNotifiableAcrossAllTutorsAsync(CancellationToken ct = default);

    /// <summary>Stages the profile for insertion; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Add(TutorProfile profile);

    /// <summary>Stages the profile for update; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Update(TutorProfile profile);
}
