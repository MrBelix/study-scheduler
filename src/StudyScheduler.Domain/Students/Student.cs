using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Students;

public sealed class Student : Entity
{
    private Student(
        Guid id,
        long tutorTelegramId,
        string name,
        decimal rate,
        DateTimeOffset createdAtUtc,
        string? subject,
        string? contact,
        TimeZoneInfo? timeZone)
        : base(id)
    {
        TutorTelegramId = tutorTelegramId;
        Name = name;
        Rate = rate;
        CreatedAtUtc = createdAtUtc;
        Subject = subject;
        Contact = contact;
        TimeZone = timeZone;
        Status = StudentStatus.Active;
    }

    /// <summary>Telegram id of the tutor this student belongs to. Ownership / scope key.</summary>
    public long TutorTelegramId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Price per lesson. Money is always <c>decimal</c>.</summary>
    public decimal Rate { get; private set; }

    public string? Subject { get; private set; }

    public string? Contact { get; private set; }

    /// <summary>Optional time zone of the student (informational); persisted by its IANA id.</summary>
    public TimeZoneInfo? TimeZone { get; private set; }

    public StudentStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Student Create(
        long tutorTelegramId,
        string name,
        decimal rate,
        DateTimeOffset createdAtUtc,
        string? subject = null,
        string? contact = null,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        return new Student(
            Guid.NewGuid(),
            tutorTelegramId,
            name.Trim(),
            rate,
            createdAtUtc,
            Normalize(subject),
            Normalize(contact),
            timeZone);
    }

    /// <summary>Replaces the editable profile fields.</summary>
    public void UpdateDetails(string name, decimal rate, string? subject, string? contact, TimeZoneInfo? timeZone)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        Name = name.Trim();
        Rate = rate;
        Subject = Normalize(subject);
        Contact = Normalize(contact);
        TimeZone = timeZone;
    }

    public void ChangeStatus(StudentStatus status) => Status = status;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
