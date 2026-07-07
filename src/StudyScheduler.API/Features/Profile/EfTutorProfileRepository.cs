using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>EF Core implementation of <see cref="ITutorProfileRepository"/> (SQL Server).</summary>
public sealed class EfTutorProfileRepository(AppDbContext db) : ITutorProfileRepository
{
    public async Task<TutorProfile?> GetAsync(long telegramUserId, CancellationToken ct = default) =>
        await db.TutorProfiles.FindAsync([telegramUserId], ct);

    public async Task AddAsync(TutorProfile profile, CancellationToken ct = default)
    {
        db.TutorProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TutorProfile profile, CancellationToken ct = default)
    {
        db.TutorProfiles.Update(profile);
        await db.SaveChangesAsync(ct);
    }
}
