using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>EF Core implementation of <see cref="ITutorProfileRepository"/> (SQL Server).</summary>
public sealed class EfTutorProfileRepository(AppDbContext db) : ITutorProfileRepository
{
    public async Task<TutorProfile?> GetAsync(long telegramUserId) =>
        await db.TutorProfiles.FindAsync(telegramUserId);

    public async Task AddAsync(TutorProfile profile)
    {
        db.TutorProfiles.Add(profile);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(TutorProfile profile)
    {
        db.TutorProfiles.Update(profile);
        await db.SaveChangesAsync();
    }
}
