using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Notifications;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// EF Core implementation of <see cref="INotificationDispatchRepository"/> (PostgreSQL). Not one
/// predicate here mentions the tutor — <see cref="AppDbContext"/>'s global query filter narrows every
/// query below to the scope's tenant, and the same tenant is stamped onto whatever <see cref="Add"/>
/// stages — except the one method whose name promises it looks past the scope.
/// </summary>
public sealed class EfNotificationDispatchRepository(AppDbContext db) : INotificationDispatchRepository
{
    public async Task<IReadOnlyList<NotificationDispatch>> GetLiveAsync(
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.NotificationDispatches : db.NotificationDispatches.AsNoTracking();
        return await query
            .Where(d => d.State == DispatchState.Delivered)
            .OrderBy(d => d.ExpiresAtUtc)
            .ToListAsync(ct);
    }

    public async Task<NotificationDispatch?> GetLiveForLessonAsync(
        Guid lessonId,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.NotificationDispatches : db.NotificationDispatches.AsNoTracking();
        return await query.SingleOrDefaultAsync(
            d => d.LessonId == lessonId && d.State == DispatchState.Delivered, ct);
    }

    public async Task<NotificationDispatch?> GetLiveForDayAsync(
        NotificationKind kind,
        DateOnly localDate,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.NotificationDispatches : db.NotificationDispatches.AsNoTracking();
        return await query.SingleOrDefaultAsync(
            d => d.Kind == kind && d.LocalDate == localDate && d.State == DispatchState.Delivered, ct);
    }

    public async Task<NotificationDispatch?> GetByMessageAsync(
        long chatId,
        int messageId,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.NotificationDispatches : db.NotificationDispatches.AsNoTracking();
        return await query
            .Where(d => d.ChatId == chatId && d.MessageId == messageId)
            .OrderByDescending(d => d.SentAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<long>> GetTutorsWithLiveDispatchesAcrossAllTutorsAsync(
        CancellationToken ct = default) =>
        // IgnoreQueryFilters is the whole point of this method: the reconciliation pass has no tenant
        // of its own and must see every tutor that still owns a live message.
        await db.NotificationDispatches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.State == DispatchState.Delivered)
            .Select(d => d.TutorTelegramId)
            .Distinct()
            .ToListAsync(ct);

    public void Add(NotificationDispatch dispatch) => db.NotificationDispatches.Add(dispatch);

    public void Update(NotificationDispatch dispatch) => db.NotificationDispatches.Update(dispatch);
}
