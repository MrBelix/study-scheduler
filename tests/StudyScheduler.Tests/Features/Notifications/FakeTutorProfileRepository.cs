using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>
/// In-memory <see cref="ITutorProfileRepository"/> mirroring the notifiable-filter semantics — and
/// the tenancy one: a profile is KEYED by the tutor id, so the scope reaches exactly the row whose
/// key is its tenant, which is how the real query filter narrows it.
/// </summary>
internal sealed class FakeTutorProfileRepository(ITutorContext tutor) : ITutorProfileRepository
{
    public List<TutorProfile> Items { get; } = [];

    public Task<TutorProfile?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(
            p => p.TelegramUserId == (tutor.CurrentTutorTelegramId ?? 0)));

    public Task<IReadOnlyList<TutorProfile>> GetNotifiableAcrossAllTutorsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TutorProfile>>(Items
            .Where(p => (p.RemindMinutes is not null || p.NotifyAfterLesson) && p.BotReachable)
            .ToList());

    public void Add(TutorProfile profile) => Items.Add(profile);

    public void Update(TutorProfile profile) { }
}
