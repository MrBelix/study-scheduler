using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// A recurring lesson rule: a <see cref="WeeklyPattern"/> active over <c>[StartDate, EndDate]</c>
/// (or forever). Occurrences are expanded virtually (in memory) at read time; a concrete
/// <see cref="Lesson"/> row is only written when a specific slot is modified. Schedule fields
/// (the pattern) are never edited in place — a schedule change ends this series and creates a new
/// one. Lifecycle is the <see cref="EndDate"/> alone: a series ended before it starts simply
/// produces nothing.
/// </summary>
public sealed class LessonSeries : Entity
{
    // EF materialization only: it sets every property (including the Pattern complex type) via
    // their private setters. The domain constructor below can't be used because EF cannot bind a
    // complex-type property to a constructor parameter.
    private LessonSeries() : base(Guid.Empty) { }

    private LessonSeries(
        Guid id,
        long tutorTelegramId,
        Guid studentId,
        string? title,
        WeeklyPattern pattern,
        DateOnly startDate,
        DateOnly? endDate,
        decimal? price,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        TutorTelegramId = tutorTelegramId;
        StudentId = studentId;
        Title = title;
        Pattern = pattern;
        StartDate = startDate;
        EndDate = endDate;
        Price = price;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Telegram id of the tutor this series belongs to. Ownership / scope key.</summary>
    public long TutorTelegramId { get; private set; }

    public Guid StudentId { get; private set; }

    public string? Title { get; private set; }

    /// <summary>The weekly recurrence rule (days, time, duration, zone).</summary>
    public WeeklyPattern Pattern { get; private set; } = null!;

    /// <summary>Local date the schedule takes effect (not necessarily a lesson day).</summary>
    public DateOnly StartDate { get; private set; }

    /// <summary>Local date of the last possible lesson; <c>null</c> means open-ended.</summary>
    public DateOnly? EndDate { get; private set; }

    /// <summary>Per-lesson price; <c>null</c> falls back to the student's rate at materialization.</summary>
    public decimal? Price { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Result<LessonSeries> Create(
        long tutorTelegramId,
        Guid studentId,
        WeeklyPattern pattern,
        DateOnly startDate,
        DateTimeOffset createdAtUtc,
        string? title = null,
        DateOnly? endDate = null,
        decimal? price = null)
    {
        // Programmer errors, not user input: callers resolve these from auth / persisted data.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);
        ArgumentNullException.ThrowIfNull(pattern);
        if (studentId == Guid.Empty)
            throw new ArgumentException("Student id is required.", nameof(studentId));

        var errors = Validate(endDate, startDate, price);
        if (errors.Count > 0)
            return Result<LessonSeries>.Failure([.. errors]);

        return Result<LessonSeries>.Success(new LessonSeries(
            Guid.NewGuid(), tutorTelegramId, studentId, Normalize(title), pattern, startDate, endDate, price, createdAtUtc));
    }

    /// <summary>Replaces the editable metadata. Changing the pattern means end + recreate.</summary>
    public Result UpdateDetails(string? title, DateOnly? endDate, decimal? price)
    {
        var errors = Validate(endDate, StartDate, price);
        if (errors.Count > 0)
            return Result.Failure([.. errors]);

        Title = Normalize(title);
        EndDate = endDate;
        Price = price;
        return Result.Success();
    }

    /// <summary>
    /// Ends the series no later than <paramref name="lastDate"/> — only ever tightened, never
    /// extended. A date before <see cref="StartDate"/> leaves the series producing no occurrences.
    /// Physical lessons are untouched; future virtual slots simply stop expanding.
    /// </summary>
    public void End(DateOnly lastDate)
    {
        if (EndDate is null || lastDate < EndDate)
            EndDate = lastDate;
    }

    /// <summary>
    /// Cancels the series effective immediately: its last possible lesson day is the day BEFORE
    /// "today" in its own time zone, so today onward stops expanding. Only ever tightens EndDate.
    /// Physical (materialized) lessons are untouched — this affects virtual expansion only.
    /// </summary>
    public void CancelAsOf(DateTimeOffset nowUtc) =>
        End(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, Pattern.TimeZone).DateTime).AddDays(-1));

    /// <summary>
    /// Occurrences intersecting <c>[fromLocal, toLocal]</c> (inclusive), clipped to the series' own
    /// <c>[StartDate, EndDate]</c> window; empty when the windows don't overlap.
    /// </summary>
    public IReadOnlyList<LessonOccurrence> GetOccurrences(DateOnly fromLocal, DateOnly toLocal)
    {
        var first = fromLocal > StartDate ? fromLocal : StartDate;
        var last = EndDate is { } end && end < toLocal ? end : toLocal;
        return last < first ? [] : Pattern.Enumerate(first, last);
    }

    private static List<Error> Validate(DateOnly? endDate, DateOnly startDate, decimal? price)
    {
        var errors = new List<Error>();
        if (endDate is { } end && end < startDate)
            errors.Add(new Error(
                "LessonSeries.EndDateBeforeStartDate", "End date must not precede start date.", "EndDate"));
        if (price is < 0)
            errors.Add(new Error("LessonSeries.NegativePrice", "Price must be zero or positive.", "Price"));
        return errors;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
