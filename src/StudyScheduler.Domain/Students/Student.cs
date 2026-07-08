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

    public static Result<Student> Create(
        long tutorTelegramId,
        string name,
        decimal rate,
        DateTimeOffset createdAtUtc,
        string? subject = null,
        string? contact = null,
        TimeZoneInfo? timeZone = null)
    {
        // Programmer error, not user input: the tutor id comes from validated auth data.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);

        if (Validate(name, rate) is { Count: > 0 } errors)
            return Result<Student>.Failure([.. errors]);

        return Result<Student>.Success(new Student(
            Guid.NewGuid(),
            tutorTelegramId,
            name.Trim(),
            rate,
            createdAtUtc,
            Normalize(subject),
            Normalize(contact),
            timeZone));
    }

    /// <summary>Replaces the editable profile fields.</summary>
    public Result UpdateDetails(string name, decimal rate, string? subject, string? contact, TimeZoneInfo? timeZone)
    {
        if (Validate(name, rate) is { Count: > 0 } errors)
            return Result.Failure([.. errors]);

        Name = name.Trim();
        Rate = rate;
        Subject = Normalize(subject);
        Contact = Normalize(contact);
        TimeZone = timeZone;
        return Result.Success();
    }

    public Result ChangeStatus(StudentStatus status)
    {
        // The API's JSON enum binding already constrains this, but the domain must not rely on
        // one particular caller — an undefined value is reported, never silently stored.
        if (!Enum.IsDefined(status))
            return Result.Failure(new Error(
                "Student.UnknownStatus", $"Unknown student status '{status}'.", "Status"));

        Status = status;
        return Result.Success();
    }

    private static List<Error> Validate(string name, decimal rate)
    {
        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(name))
            errors.Add(new Error("Student.NameRequired", "Name is required.", "Name"));
        if (rate < 0)
            errors.Add(new Error("Student.NegativeRate", "Rate must be zero or positive.", "Rate"));
        return errors;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
