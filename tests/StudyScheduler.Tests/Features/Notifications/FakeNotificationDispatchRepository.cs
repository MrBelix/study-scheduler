using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Tests.Features.Lessons;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>
/// In-memory <see cref="INotificationDispatchRepository"/> mirroring the EF semantics — tenancy
/// included: every read below is narrowed to <see cref="ITutorContext.CurrentTutorTelegramId"/>
/// exactly as <c>AppDbContext</c>'s global query filter narrows the real ones, and <see cref="Add"/>
/// stamps that tenant onto the row exactly as its <c>SaveChanges</c> does. The one cross-tenant
/// method is the only one here that looks past the current scope, as its name promises.
/// </summary>
internal sealed class FakeNotificationDispatchRepository(ITutorContext tutor) : INotificationDispatchRepository
{
    /// <summary>Every stored row, of every tenant — the assertion surface, not the query surface.</summary>
    public List<NotificationDispatch> Items { get; } = [];

    private IEnumerable<NotificationDispatch> Mine =>
        Items.Where(d => d.TutorTelegramId == (tutor.CurrentTutorTelegramId ?? 0));

    public Task<IReadOnlyList<NotificationDispatch>> GetLiveAsync(
        bool track = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationDispatch>>(Mine
            .Where(d => d.State == DispatchState.Delivered)
            .OrderBy(d => d.ExpiresAtUtc)
            .ToList());

    public Task<NotificationDispatch?> GetLiveForLessonAsync(
        Guid lessonId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Mine.SingleOrDefault(
            d => d.LessonId == lessonId && d.State == DispatchState.Delivered));

    public Task<NotificationDispatch?> GetLiveForDayAsync(
        NotificationKind kind, DateOnly localDate, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Mine.SingleOrDefault(
            d => d.Kind == kind && d.LocalDate == localDate && d.State == DispatchState.Delivered));

    public Task<NotificationDispatch?> GetByMessageAsync(
        long chatId, int messageId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Mine
            .Where(d => d.ChatId == chatId && d.MessageId == messageId)
            .OrderByDescending(d => d.SentAtUtc)
            .FirstOrDefault());

    public Task<IReadOnlyList<long>> GetTutorsWithLiveDispatchesAcrossAllTutorsAsync(
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<long>>(Items
            .Where(d => d.State == DispatchState.Delivered)
            .Select(d => d.TutorTelegramId)
            .Distinct()
            .ToList());

    public void Add(NotificationDispatch dispatch) => Items.Add(TenantStamp.Apply(dispatch, tutor));

    public void Update(NotificationDispatch dispatch) { }
}
