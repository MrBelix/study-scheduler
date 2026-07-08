namespace StudyScheduler.Domain.Lessons;

/// <summary>Persistence contract for the notification dedup log (<see cref="LessonNotification"/>).</summary>
public interface ILessonNotificationRepository
{
    /// <summary>Which of <paramref name="slotKeys"/> already got a <paramref name="kind"/> notification.</summary>
    Task<HashSet<string>> GetSentSlotKeysAsync(
        long tutorTelegramId,
        LessonNotificationKind kind,
        IReadOnlyCollection<string> slotKeys,
        CancellationToken ct = default);

    /// <summary>Stages the record for insertion; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Add(LessonNotification notification);
}
