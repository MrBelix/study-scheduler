using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>EF Core implementation of <see cref="ITutorProfileRepository"/> (PostgreSQL).</summary>
public sealed class EfTutorProfileRepository(AppDbContext db) : ITutorProfileRepository
{
    /// <summary>
    /// A profile is keyed BY the tutor id, so the global query filter sits on its primary key: the
    /// filtered set holds at most the one row this scope owns, and asking for "the profile" is the
    /// same question as asking for it by id used to be. Tracked — the upsert path mutates it.
    /// </summary>
    public async Task<TutorProfile?> GetAsync(CancellationToken ct = default) =>
        await db.TutorProfiles.SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<TutorProfile>> GetNotifiableAcrossAllTutorsAsync(CancellationToken ct = default) =>
        // IgnoreQueryFilters is the whole point of this method: the poller has no tenant of its own
        // and must see every tutor's profile.
        await db.TutorProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => (p.RemindMinutes != null || p.NotifyAfterLesson) && p.BotReachable)
            .ToListAsync(ct);

    public void Add(TutorProfile profile) => db.TutorProfiles.Add(profile);

    public void Update(TutorProfile profile) => db.TutorProfiles.Update(profile);
}
