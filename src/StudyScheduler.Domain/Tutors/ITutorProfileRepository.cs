namespace StudyScheduler.Domain.Tutors;

/// <summary>Persistence contract for <see cref="TutorProfile"/> (one row per tutor).</summary>
public interface ITutorProfileRepository
{
    Task<TutorProfile?> GetAsync(long telegramUserId, CancellationToken ct = default);

    /// <summary>Stages the profile for insertion; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Add(TutorProfile profile);

    /// <summary>Stages the profile for update; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Update(TutorProfile profile);
}
