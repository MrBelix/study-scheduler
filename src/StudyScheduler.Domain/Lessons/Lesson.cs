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

    public static Lesson Create(
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);
        if (studentId == Guid.Empty)
            throw new ArgumentException("Student id is required.", nameof(studentId));
        ValidateDuration(durationMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(price);
        if (seriesId.HasValue != occurrenceDate.HasValue)
            throw new ArgumentException("SeriesId and OccurrenceDate must be provided together.", nameof(occurrenceDate));

        return new Lesson(
            Guid.NewGuid(),
            tutorTelegramId,
            studentId,
            startUtc,
            durationMinutes,
            price,
            createdAtUtc,
            NormalizeText(topic, MaxTopicLength, nameof(topic)),
            NormalizeText(description, MaxDescriptionLength, nameof(description)),
            seriesId,
            occurrenceDate);
    }

    public void Reschedule(DateTimeOffset startUtc, int durationMinutes)
    {
        ValidateDuration(durationMinutes);

        StartUtc = startUtc;
        DurationMinutes = durationMinutes;
        EndUtc = startUtc.AddMinutes(durationMinutes);
    }

    public void ChangeStatus(LessonStatus status)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentException($"Unknown lesson status '{status}'.", nameof(status));

        Status = status;
    }

    public void SetPrice(decimal price)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(price);
        Price = price;
    }

    public void SetPaid(bool isPaid) => IsPaid = isPaid;

    public void UpdateTopic(string? topic) =>
        Topic = NormalizeText(topic, MaxTopicLength, nameof(topic));

    public void UpdateDescription(string? description) =>
        Description = NormalizeText(description, MaxDescriptionLength, nameof(description));

    private static void ValidateDuration(int durationMinutes)
    {
        if (durationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
            throw new ArgumentOutOfRangeException(
                nameof(durationMinutes),
                durationMinutes,
                $"Duration must be between {MinDurationMinutes} and {MaxDurationMinutes} minutes.");
    }

    private static string? NormalizeText(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"Must not exceed {maxLength} characters.", paramName);

        return trimmed;
    }
}
