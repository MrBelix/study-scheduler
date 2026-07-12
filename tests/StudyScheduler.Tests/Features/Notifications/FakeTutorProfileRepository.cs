using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>In-memory <see cref="ITutorProfileRepository"/> mirroring the notifiable-filter semantics.</summary>
internal sealed class FakeTutorProfileRepository : ITutorProfileRepository
{
    public List<TutorProfile> Items { get; } = [];

    public Task<TutorProfile?> GetAsync(long telegramUserId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(p => p.TelegramUserId == telegramUserId));

    public Task<IReadOnlyList<TutorProfile>> GetNotifiableAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TutorProfile>>(Items
            .Where(p => (p.RemindMinutes is not null || p.NotifyAfterLesson) && p.BotReachable)
            .ToList());

    public void Add(TutorProfile profile) => Items.Add(profile);

    public void Update(TutorProfile profile) { }
}
