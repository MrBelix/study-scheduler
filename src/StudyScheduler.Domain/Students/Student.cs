using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Students;

public sealed class Student : Entity
{
    private Student(Guid id, long ownerTelegramId, string name)
        : base(id)
    {
        OwnerTelegramId = ownerTelegramId;
        Name = name;
    }

    /// <summary>Telegram id of the user (tutor/owner) this student belongs to.</summary>
    public long OwnerTelegramId { get; private set; }

    public string Name { get; private set; }

    public static Student Create(long ownerTelegramId, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerTelegramId);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        return new Student(Guid.NewGuid(), ownerTelegramId, name.Trim());
    }
}
