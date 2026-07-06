namespace StudyScheduler.Domain.Tutors;

/// <summary>Persistence contract for <see cref="TutorProfile"/> (one row per tutor).</summary>
public interface ITutorProfileRepository
{
    Task<TutorProfile?> GetAsync(long telegramUserId);

    Task AddAsync(TutorProfile profile);

    Task UpdateAsync(TutorProfile profile);
}
