namespace StudyScheduler.Domain.Primitives;

/// <summary>
/// An aggregate that belongs to exactly one tutor. The owner's Telegram id is the tenancy key:
/// persistence filters every read by it and stamps it on insert, so ownership stops being an
/// argument every caller has to carry. Implemented by the aggregate roots only — value objects and
/// complex types travel inside them.
/// </summary>
public interface ITutorOwned
{
    /// <summary>Telegram id of the owning tutor. Ownership / scope key.</summary>
    long TutorTelegramId { get; }
}
