using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Students;

public sealed class Student : Entity, ITutorOwned
{
    private Student(
        Guid id,
        string name,
        decimal rate,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        Name = name;
        Rate = rate;
        CreatedAtUtc = createdAtUtc;
        Status = StudentStatus.Active;
    }

    /// <summary>
    /// Telegram id of the tutor this student belongs to. Ownership / scope key: persistence stamps it
    /// from the scope's tenant on insert and filters every read by it, so nothing in the domain — or
    /// above it — has to carry the owner around.
    /// </summary>
    public long TutorTelegramId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Price per lesson. Money is always <c>decimal</c>.</summary>
    public decimal Rate { get; private set; }

    public StudentStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// A new active student of the current tenant — whoever that is, is persistence's business
    /// (see <see cref="TutorTelegramId"/>), which is why it is not an argument here.
    /// </summary>
    public static Result<Student> Create(
        string name,
        decimal rate,
        DateTimeOffset createdAtUtc)
    {
        if (Validate(name, rate) is { Count: > 0 } errors)
            return Result<Student>.Failure([.. errors]);

        return Result<Student>.Success(new Student(
            Guid.NewGuid(),
            name.Trim(),
            rate,
            createdAtUtc));
    }

    /// <summary>Replaces the editable profile fields.</summary>
    public Result UpdateDetails(string name, decimal rate)
    {
        if (Validate(name, rate) is { Count: > 0 } errors)
            return Result.Failure([.. errors]);

        Name = name.Trim();
        Rate = rate;
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
}
