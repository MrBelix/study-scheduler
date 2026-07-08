using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// A concrete lesson occurrence — either created directly (one-off) or materialized on demand
/// from a <see cref="LessonSeries"/> when a specific slot is first modified (topic/description,
/// cancel, reschedule…). Untouched series slots are never stored; they are expanded virtually at
/// read time. Times are stored in UTC; <see cref="EndUtc"/> is denormalized (always
/// <c>StartUtc + DurationMinutes</c>) so overlap queries stay SQL-translatable and indexed.
/// </summary>
public sealed class Lesson : Entity
{
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 600;
    public const int MaxTopicLength = 200;
    public const int MaxDescriptionLength = 2000;

    private Lesson(
        Guid id,
        long tutorTelegramId,
        Guid studentId,
        DateTimeOffset startUtc,
        int durationMinutes,
        decimal price,
        DateTimeOffset createdAtUtc,
        string? topic,
        string? description,
        Guid? seriesId,
        DateOnly? occurrenceDate)
        : base(id)
    {
        TutorTelegramId = tutorTelegramId;
        StudentId = studentId;
        StartUtc = startUtc;
        DurationMinutes = durationMinutes;
        EndUtc = startUtc.AddMinutes(durationMinutes);
        Price = price;
        CreatedAtUtc = createdAtUtc;
        Topic = topic;
        Description = description;
        SeriesId = seriesId;
        OccurrenceDate = occurrenceDate;
        Status = LessonStatus.Scheduled;
        IsPaid = false;
    }

    /// <summary>Telegram id of the tutor this lesson belongs to. Ownership / scope key.</summary>
    public long TutorTelegramId { get; private set; }

    public Guid StudentId { get; private set; }

    /// <summary>Set when the lesson was materialized from a <see cref="LessonSeries"/>.</summary>
    public Guid? SeriesId { get; private set; }

    /// <summary>
    /// Canonical local date of the series slot this lesson materializes — the original scheduled
    /// date, which never changes even if the lesson is rescheduled to another time. Together with
    /// <see cref="SeriesId"/> it strictly maps the physical record back to its virtual slot in the
    /// series (unique index), so on-the-fly expansion knows the slot is taken.
    /// </summary>
    public DateOnly? OccurrenceDate { get; private set; }

    public DateTimeOffset StartUtc { get; private set; }

    /// <summary>Invariant: always <c>StartUtc + DurationMinutes</c>.</summary>
    public DateTimeOffset EndUtc { get; private set; }

    public int DurationMinutes { get; private set; }

    public LessonStatus Status { get; private set; }

    /// <summary>Price snapshot taken at creation; money is always <c>decimal</c>.</summary>
    public decimal Price { get; private set; }

    public bool IsPaid { get; private set; }

    /// <summary>Short subject of the lesson.</summary>
    public string? Topic { get; private set; }

    /// <summary>Free-form notes / details for the lesson.</summary>
    public string? Description { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Result<Lesson> Create(
        long tutorTelegramId,
        Guid studentId,
        DateTimeOffset startUtc,
        int durationMinutes,
        decimal price,
        DateTimeOffset createdAtUtc,
        string? topic = null,
        string? description = null,
        Guid? seriesId = null,
        DateOnly? occurrenceDate = null)
    {
        // Programmer errors, not user input: callers resolve these from auth / persisted data.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);
        if (studentId == Guid.Empty)
            throw new ArgumentException("Student id is required.", nameof(studentId));
        if (seriesId.HasValue != occurrenceDate.HasValue)
            throw new ArgumentException("SeriesId and OccurrenceDate must be provided together.", nameof(occurrenceDate));

        var errors = new List<Error>();
        if (ValidateDuration(durationMinutes) is { } durationError)
            errors.Add(durationError);
        if (ValidatePrice(price) is { } priceError)
            errors.Add(priceError);
        if (ValidateText(topic, MaxTopicLength, "Topic") is { } topicError)
            errors.Add(topicError);
        if (ValidateText(description, MaxDescriptionLength, "Description") is { } descriptionError)
            errors.Add(descriptionError);
        if (errors.Count > 0)
            return Result<Lesson>.Failure([.. errors]);

        return Result<Lesson>.Success(new Lesson(
            Guid.NewGuid(),
            tutorTelegramId,
            studentId,
            startUtc,
            durationMinutes,
            price,
            createdAtUtc,
            Normalize(topic),
            Normalize(description),
            seriesId,
            occurrenceDate));
    }

    public Result Reschedule(DateTimeOffset startUtc, int durationMinutes)
    {
        if (ValidateDuration(durationMinutes) is { } error)
            return Result.Failure(error);

        StartUtc = startUtc;
        DurationMinutes = durationMinutes;
        EndUtc = startUtc.AddMinutes(durationMinutes);
        return Result.Success();
    }

    public Result ChangeStatus(LessonStatus status)
    {
        // The API's JSON enum binding already constrains this, but the domain must not rely on
        // one particular caller — an undefined value is reported, never silently stored.
        if (!Enum.IsDefined(status))
            return Result.Failure(new Error(
                "Lesson.UnknownStatus", $"Unknown lesson status '{status}'.", "Status"));

        Status = status;
        return Result.Success();
    }

    public Result SetPrice(decimal price)
    {
        if (ValidatePrice(price) is { } error)
            return Result.Failure(error);

        Price = price;
        return Result.Success();
    }

    public void SetPaid(bool isPaid) => IsPaid = isPaid;

    public Result UpdateTopic(string? topic)
    {
        if (ValidateText(topic, MaxTopicLength, "Topic") is { } error)
            return Result.Failure(error);

        Topic = Normalize(topic);
        return Result.Success();
    }

    public Result UpdateDescription(string? description)
    {
        if (ValidateText(description, MaxDescriptionLength, "Description") is { } error)
            return Result.Failure(error);

        Description = Normalize(description);
        return Result.Success();
    }

    private static Error? ValidateDuration(int durationMinutes) =>
        durationMinutes is < MinDurationMinutes or > MaxDurationMinutes
            ? new Error(
                "Lesson.DurationOutOfRange",
                $"Duration must be between {MinDurationMinutes} and {MaxDurationMinutes} minutes.",
                "DurationMinutes")
            : null;

    private static Error? ValidatePrice(decimal price) =>
        price < 0
            ? new Error("Lesson.NegativePrice", "Price must be zero or positive.", "Price")
            : null;

    // Message shape mirrors the API's historical ValidationProblem strings — the field name is
    // part of the payload contract the frontend maps onto its form fields.
    private static Error? ValidateText(string? value, int maxLength, string field) =>
        value?.Trim().Length > maxLength
            ? new Error(
                $"Lesson.{field}TooLong", $"{field} must not exceed {maxLength} characters.", field)
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
