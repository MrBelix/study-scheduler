namespace StudyScheduler.Domain.Tutors;

/// <summary>Persistence contract for <see cref="TutorProfile"/> (one row per tutor).</summary>
public interface ITutorProfileRepository
{
    Task<TutorProfile?> GetAsync(long telegramUserId, CancellationToken ct = default);

    Task AddAsync(TutorProfile profile, CancellationToken ct = default);

    Task UpdateAsync(TutorProfile profile, CancellationToken ct = default);
}
