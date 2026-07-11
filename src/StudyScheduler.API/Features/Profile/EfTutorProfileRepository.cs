using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>EF Core implementation of <see cref="ITutorProfileRepository"/> (SQL Server).</summary>
public sealed class EfTutorProfileRepository(AppDbContext db) : ITutorProfileRepository
{
    public async Task<TutorProfile?> GetAsync(long telegramUserId, CancellationToken ct = default) =>
        await db.TutorProfiles.FindAsync([telegramUserId], ct);

    public void Add(TutorProfile profile) => db.TutorProfiles.Add(profile);

    public void Update(TutorProfile profile) => db.TutorProfiles.Update(profile);
}
