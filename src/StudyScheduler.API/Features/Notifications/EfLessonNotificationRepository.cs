using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>EF Core implementation of <see cref="ILessonNotificationRepository"/> (SQL Server).</summary>
public sealed class EfLessonNotificationRepository(AppDbContext db) : ILessonNotificationRepository
{
    public async Task<HashSet<string>> GetSentSlotKeysAsync(
        long tutorTelegramId,
        LessonNotificationKind kind,
        IReadOnlyCollection<string> slotKeys,
        CancellationToken ct = default)
    {
        var keys = await db.LessonNotifications
            .AsNoTracking()
            .Where(n => n.TutorTelegramId == tutorTelegramId && n.Kind == kind && slotKeys.Contains(n.SlotKey))
            .Select(n => n.SlotKey)
            .ToListAsync(ct);

        return [.. keys];
    }

    public void Add(LessonNotification notification) => db.LessonNotifications.Add(notification);
}
