using System.Linq.Expressions;
using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// A concrete lesson — either created directly (one-off) or written out by a
/// <see cref="LessonSeries"/>, which generates a row for every occurrence inside the planning
/// horizon. Every lesson exists as a row: nothing is projected at read time. Times are stored in
/// UTC; <see cref="EndUtc"/> is denormalized (always <c>StartUtc + DurationMinutes</c>) so overlap
/// queries stay SQL-translatable and indexed.
/// </summary>
public sealed class Lesson : Entity, ITutorOwned
{
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 600;
    public const int MaxTopicLength = 200;
    public const int MaxDescriptionLength = 2000;

    /// <summary>
    /// What a DEBT is, once, for everybody who counts one: a lesson already taught
    /// (<see cref="LessonStatus.Completed"/>) that was never paid for. A cancelled lesson owes
    /// nothing and a scheduled one is not owed yet, so neither can be a debt; and no date bounds it,
    /// because a debt does not stop being owed because the reporting period moved on.
    /// An expression rather than a plain property so the same definition can be handed to the
    /// database — the Money screen's debtor ledger and one student's debt banner must never be able
    /// to disagree about which rows are money owed.
    /// </summary>
    public static Expression<Func<Lesson, bool>> IsDebt { get; } =
        lesson => lesson.Status == LessonStatus.Completed && !lesson.IsPaid;

    // EF materialization only: it sets every property via their private setters.
    private Lesson() : base(Guid.Empty) { }

    private Lesson(
        Guid id,
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
        // A free lesson owes nothing, so it starts settled instead of showing up as a debt.
        IsPaid = price == 0m;
    }

    /// <summary>
    /// Telegram id of the tutor this lesson belongs to. Ownership / scope key: persistence stamps it
    /// from the scope's tenant on insert and filters every read by it, so nothing in the domain — or
    /// above it — has to carry the owner around.
    /// </summary>
    public long TutorTelegramId { get; private set; }

    public Guid StudentId { get; private set; }

    /// <summary>Set when the lesson was generated from a <see cref="LessonSeries"/>.</summary>
    public Guid? SeriesId { get; private set; }

    /// <summary>
    /// Canonical local date of the series slot this lesson fills — the originally scheduled date,
    /// which never changes even if the lesson is rescheduled to another time. Together with
    /// <see cref="SeriesId"/> it is the slot's identity (unique index), so generation is idempotent.
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

    /// <summary>
    /// Whether a person deliberately touched this occurrence — rescheduled it, re-priced it, named it,
    /// settled it or cancelled it. Rows written by the schedule generator start out untouched; the flag
    /// is a one-way latch marking a per-lesson fact that must survive regeneration, so the generator
    /// never reconsiders the date it sits on. Both surfaces count as a person: the app's
    /// <c>PATCH /lessons/{id}</c> and the bot's "як пройшло?" buttons make the very same statement
    /// about the very same lesson.
    /// </summary>
    public bool IsCustomized { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// A <see cref="LessonStatus.Scheduled"/> lesson starting at <paramref name="startUtc"/>, owned by
    /// the current tenant (see <see cref="TutorTelegramId"/>).
    /// <paramref name="seriesId"/> and <paramref name="occurrenceDate"/> are supplied together, and
    /// only when the lesson fills a series slot. Duration, price and text lengths are
    /// user-fixable and come back as errors; the slot arguments are the caller's contract and throw.
    /// </summary>
    public static Result<Lesson> Create(
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
        EnsureCreationInputs(studentId, seriesId, occurrenceDate);

        var errors = Validate(durationMinutes, price, topic, description);
        if (errors.Count > 0)
            return Result<Lesson>.Failure([.. errors]);

        return Result<Lesson>.Success(new Lesson(
            Guid.NewGuid(),
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

    /// <summary>
    /// Moves the lesson to another start instant, keeping its duration. Any instant is valid — but a
    /// lesson already recorded as <see cref="LessonStatus.Completed"/> does not move at all: it
    /// happened when it happened.
    /// </summary>
    public Result Reschedule(DateTimeOffset startUtc)
    {
        if (AlreadyCompleted("StartUtc") is { } settled)
            return Result.Failure(settled);

        StartUtc = startUtc;
        EndUtc = startUtc.AddMinutes(DurationMinutes);
        return Result.Success();
    }

    /// <summary>
    /// Changes how long the lesson runs, keeping its start. Refused on a completed lesson: how long
    /// it ran is part of what happened.
    /// </summary>
    public Result ChangeDuration(int durationMinutes)
    {
        if (AlreadyCompleted("DurationMinutes") is { } settled)
            return Result.Failure(settled);

        if (ValidateDuration(durationMinutes) is { } error)
            return Result.Failure(error);

        DurationMinutes = durationMinutes;
        EndUtc = StartUtc.AddMinutes(durationMinutes);
        return Result.Success();
    }

    /// <summary>
    /// Moves the lesson to another lifecycle status. Cancelling also drops <see cref="IsPaid"/> — a
    /// cancelled lesson owes and is owed nothing, and the flag is never restored by un-cancelling.
    /// Cancelling a COMPLETED lesson is refused: it happened, and its price is already counted as
    /// money owed or received. Every other transition stays open — the correction back to
    /// <see cref="LessonStatus.Scheduled"/> included, because a settle recorded by mistake must be
    /// undoable.
    /// </summary>
    public Result ChangeStatus(LessonStatus status)
    {
        // The API's JSON enum binding already constrains this, but the domain must not rely on
        // one particular caller — an undefined value is reported, never silently stored.
        if (!Enum.IsDefined(status))
            return Result.Failure(new Error(
                "Lesson.UnknownStatus", $"Unknown lesson status '{status}'.", "Status"));

        if (status == LessonStatus.Cancelled && AlreadyCompleted("Status") is { } settled)
            return Result.Failure(settled);

        Status = status;
        if (status == LessonStatus.Cancelled)
            IsPaid = false;
        return Result.Success();
    }

    /// <summary>
    /// Replaces the price snapshot. Dropping it to zero settles the lesson (nothing is owed);
    /// a non-zero price leaves <see cref="IsPaid"/> exactly as it was. Callers that also apply an
    /// explicit paid flag must do so after this call, so the user's choice wins.
    /// </summary>
    public Result SetPrice(decimal price)
    {
        if (ValidatePrice(price) is { } error)
            return Result.Failure(error);

        Price = price;
        // Cancelled lessons stay unpaid — free or not (see ChangeStatus).
        if (price == 0m && Status != LessonStatus.Cancelled)
            IsPaid = true;
        return Result.Success();
    }

    /// <summary>Sets the payment flag. A cancelled lesson can never be paid.</summary>
    public Result SetPaid(bool isPaid)
    {
        if (isPaid && Status == LessonStatus.Cancelled)
            return Result.Failure(new Error(
                "Lesson.CancelledCannotBePaid", "A cancelled lesson cannot be marked as paid.", "IsPaid"));

        IsPaid = isPaid;
        return Result.Success();
    }

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

    /// <summary>
    /// Latches <see cref="IsCustomized"/>: this occurrence now carries a decision of its own and the
    /// schedule generator must leave its date alone forever after. Idempotent and one-way — nothing
    /// un-customizes a lesson, because the decision was still made.
    /// </summary>
    public void MarkCustomized() => IsCustomized = true;

    /// <summary>
    /// The refusal a completed lesson answers a re-timing or a cancellation with — null while it has
    /// not been recorded as completed. A completed lesson is a settled fact: it carries the payment
    /// the debt dashboard counts, so WHEN it happened and THAT it happened are no longer editable.
    /// Settling it, naming it and correcting the status back to Scheduled still are.
    /// <paramref name="field"/> names the offending input, so the 400 highlights the right form field.
    /// </summary>
    private Error? AlreadyCompleted(string field) =>
        Status == LessonStatus.Completed
            ? new Error(
                "Lesson.AlreadyCompleted",
                "A completed lesson can no longer be rescheduled or cancelled. Set its status back to Scheduled first.",
                field)
            : null;

    // Programmer errors, not user input: callers resolve these from persisted data.
    private static void EnsureCreationInputs(Guid studentId, Guid? seriesId, DateOnly? occurrenceDate)
    {
        if (studentId == Guid.Empty)
            throw new ArgumentException("Student id is required.", nameof(studentId));
        // Both halves of the (SeriesId, OccurrenceDate) slot key, or neither.
        if (seriesId.HasValue != occurrenceDate.HasValue)
            throw new ArgumentException("SeriesId and OccurrenceDate must be provided together.", nameof(occurrenceDate));
    }

    /// <summary>The user-fixable violations of a candidate lesson, reported together.</summary>
    private static List<Error> Validate(int durationMinutes, decimal price, string? topic, string? description)
    {
        var errors = new List<Error>();
        if (ValidateDuration(durationMinutes) is { } durationError)
            errors.Add(durationError);
        if (ValidatePrice(price) is { } priceError)
            errors.Add(priceError);
        if (ValidateText(topic, MaxTopicLength, "Topic") is { } topicError)
            errors.Add(topicError);
        if (ValidateText(description, MaxDescriptionLength, "Description") is { } descriptionError)
            errors.Add(descriptionError);
        return errors;
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
