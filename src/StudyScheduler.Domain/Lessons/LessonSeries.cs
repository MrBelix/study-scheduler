using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// A recurring lesson rule: a <see cref="WeeklyPattern"/> active over <c>[StartDate, EndDate]</c>
/// (or forever). The series is a GENERATION RULE — its occurrences are written out as physical
/// <see cref="Lesson"/> rows filling the planning horizon — so every field of it, the weekly
/// schedule included, is edited in place through <see cref="Update"/>. What that does to the rows
/// already generated from the previous schedule is the caller's business, not the rule's.
/// Lifecycle is the <see cref="EndDate"/> alone: a series ended before it starts simply produces
/// nothing.
/// </summary>
public sealed class LessonSeries : Entity, ITutorOwned
{
    // EF materialization only: it sets every property (including the Pattern complex type) via
    // their private setters. The domain constructor below can't be used because EF cannot bind a
    // complex-type property to a constructor parameter.
    private LessonSeries() : base(Guid.Empty) { }

    private LessonSeries(
        Guid id,
        Guid studentId,
        string? title,
        WeeklyPattern pattern,
        DateOnly startDate,
        DateOnly? endDate,
        decimal? price,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        StudentId = studentId;
        Title = title;
        Pattern = pattern;
        StartDate = startDate;
        EndDate = endDate;
        Price = price;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Telegram id of the tutor this series belongs to. Ownership / scope key: persistence stamps it
    /// from the scope's tenant on insert and filters every read by it, so nothing in the domain — or
    /// above it — has to carry the owner around.
    /// </summary>
    public long TutorTelegramId { get; private set; }

    public Guid StudentId { get; private set; }

    public string? Title { get; private set; }

    /// <summary>The weekly recurrence rule (days, time, duration, zone).</summary>
    public WeeklyPattern Pattern { get; private set; } = null!;

    /// <summary>Local date the schedule takes effect (not necessarily a lesson day).</summary>
    public DateOnly StartDate { get; private set; }

    /// <summary>Local date of the last possible lesson; <c>null</c> means open-ended.</summary>
    public DateOnly? EndDate { get; private set; }

    /// <summary>Per-lesson price; <c>null</c> falls back to the student's rate when rows are generated.</summary>
    public decimal? Price { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// A series running <paramref name="pattern"/> from <paramref name="startDate"/> on, owned by the
    /// current tenant (see <see cref="TutorTelegramId"/>). End date and price are user-fixable and
    /// come back as errors; the student and the pattern are the caller's contract and throw.
    /// </summary>
    public static Result<LessonSeries> Create(
        Guid studentId,
        WeeklyPattern pattern,
        DateOnly startDate,
        DateTimeOffset createdAtUtc,
        string? title = null,
        DateOnly? endDate = null,
        decimal? price = null)
    {
        EnsureCreationInputs(studentId, pattern);

        var errors = Validate(endDate, startDate, price);
        if (errors.Count > 0)
            return Result<LessonSeries>.Failure([.. errors]);

        return Result<LessonSeries>.Success(new LessonSeries(
            Guid.NewGuid(), studentId, Normalize(title), pattern, startDate, endDate, price, createdAtUtc));
    }

    /// <summary>
    /// Replaces everything a tutor can edit: the name, the weekly schedule, the date the series runs
    /// until and the price. Nothing is mutated when the candidate violates an invariant, so a refused
    /// edit leaves the rule exactly as it was. The start date is fixed — a series takes effect once.
    /// </summary>
    public Result Update(string? title, WeeklyPattern pattern, DateOnly? endDate, decimal? price)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var errors = Validate(endDate, StartDate, price);
        if (errors.Count > 0)
            return Result.Failure([.. errors]);

        Title = Normalize(title);
        Pattern = pattern;
        EndDate = endDate;
        Price = price;
        return Result.Success();
    }

    /// <summary>
    /// Ends the series no later than <paramref name="lastDate"/> — only ever tightened, never
    /// extended. A date before <see cref="StartDate"/> leaves the series producing no occurrences.
    /// Physical lessons are untouched; the rule simply stops producing occurrences past that date.
    /// </summary>
    public void End(DateOnly lastDate)
    {
        if (EndDate is null || lastDate < EndDate)
            EndDate = lastDate;
    }

    /// <summary>
    /// Cancels the series effective immediately: its last possible lesson day is the day BEFORE
    /// "today" in its own time zone, so it produces nothing from today on. Only ever tightens
    /// EndDate. Physical lessons are untouched — sweeping them is the caller's decision.
    /// </summary>
    public void CancelAsOf(DateTimeOffset nowUtc) => End(Pattern.LocalDateOf(nowUtc).AddDays(-1));

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

    /// <summary>
    /// Whether <paramref name="date"/> is a lesson day of this series: inside the active window
    /// <see cref="GetOccurrences"/> clips to, and on a weekday the pattern runs.
    /// </summary>
    public bool HasSlotOn(DateOnly date) => IsActiveOn(date) && Pattern.Days.Contains(date.DayOfWeek);

    /// <summary>
    /// Whether <paramref name="date"/> lies in the <c>[StartDate, EndDate]</c> window the series can
    /// produce lessons in (open-ended when <see cref="EndDate"/> is null) — the weekday mask is not
    /// consulted here.
    /// </summary>
    private bool IsActiveOn(DateOnly date) =>
        date >= StartDate && (EndDate is not { } end || date <= end);

    // Programmer errors, not user input: callers resolve these from persisted data.
    private static void EnsureCreationInputs(Guid studentId, WeeklyPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (studentId == Guid.Empty)
            throw new ArgumentException("Student id is required.", nameof(studentId));
    }

    /// <summary>The user-fixable violations of a candidate window and price, reported together.</summary>
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
