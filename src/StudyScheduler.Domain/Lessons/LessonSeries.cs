using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// A recurring lesson rule: "on <see cref="Weekdays"/> at <see cref="StartTimeLocal"/> in
/// <see cref="TimeZone"/>, from <see cref="StartDate"/> until <see cref="EndDate"/> (or forever)".
/// Occurrences are expanded virtually (in memory) from this rule at read time; a concrete
/// <see cref="Lesson"/> row is only written when a specific slot is modified. The time is defined
/// in the tutor's local wall clock, so occurrences stay at the same local time across DST
/// transitions.
/// </summary>
public sealed class LessonSeries : Entity
{
    private LessonSeries(
        Guid id,
        long tutorTelegramId,
        Guid studentId,
        string? title,
        DateOnly startDate,
        DateOnly? endDate,
        Weekdays weekdays,
        TimeOnly startTimeLocal,
        int durationMinutes,
        TimeZoneInfo timeZone,
        decimal? price,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        TutorTelegramId = tutorTelegramId;
        StudentId = studentId;
        Title = title;
        StartDate = startDate;
        EndDate = endDate;
        Weekdays = weekdays;
        StartTimeLocal = startTimeLocal;
        DurationMinutes = durationMinutes;
        TimeZone = timeZone;
        Price = price;
        CreatedAtUtc = createdAtUtc;
        IsActive = true;
    }

    /// <summary>Telegram id of the tutor this series belongs to. Ownership / scope key.</summary>
    public long TutorTelegramId { get; private set; }

    public Guid StudentId { get; private set; }

    public string? Title { get; private set; }

    /// <summary>Local date the schedule takes effect (not necessarily a lesson day).</summary>
    public DateOnly StartDate { get; private set; }

    /// <summary>Local date of the last possible lesson; <c>null</c> means the series is open-ended.</summary>
    public DateOnly? EndDate { get; private set; }

    /// <summary>Days of week the lessons run on — one or more flags.</summary>
    public Weekdays Weekdays { get; private set; }

    /// <summary>Wall-clock start time in <see cref="TimeZone"/>.</summary>
    public TimeOnly StartTimeLocal { get; private set; }

    public int DurationMinutes { get; private set; }

    /// <summary>Time zone the series is defined in (snapshot of the tutor's profile).</summary>
    public TimeZoneInfo TimeZone { get; private set; }

    /// <summary>Per-lesson price; <c>null</c> falls back to the student's rate at materialization.</summary>
    public decimal? Price { get; private set; }

    /// <summary>
    /// False only when the series was ended before it ever produced an occurrence — such a series
    /// is skipped entirely by virtual expansion. A series ended mid-life stays active with a
    /// bounded <see cref="EndDate"/>, so its past occurrences keep expanding.
    /// </summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Result<LessonSeries> Create(
        long tutorTelegramId,
        Guid studentId,
        DateOnly startDate,
        Weekdays weekdays,
        TimeOnly startTimeLocal,
        int durationMinutes,
        TimeZoneInfo timeZone,
        DateTimeOffset createdAtUtc,
        string? title = null,
        DateOnly? endDate = null,
        decimal? price = null)
    {
        // Programmer errors, not user input: callers resolve these from auth / persisted data.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (studentId == Guid.Empty)
            throw new ArgumentException("Student id is required.", nameof(studentId));

        var errors = new List<Error>();
        if (!weekdays.IsValidSet())
            errors.Add(new Error(
                "LessonSeries.InvalidWeekdays", "At least one valid weekday is required.", "Weekdays"));
        if (durationMinutes is < Lesson.MinDurationMinutes or > Lesson.MaxDurationMinutes)
            errors.Add(new Error(
                "LessonSeries.DurationOutOfRange",
                $"Duration must be between {Lesson.MinDurationMinutes} and {Lesson.MaxDurationMinutes} minutes.",
                "DurationMinutes"));
        if (ValidateEndDate(endDate, startDate) is { } endDateError)
            errors.Add(endDateError);
        if (ValidatePrice(price) is { } priceError)
            errors.Add(priceError);
        if (errors.Count > 0)
            return Result<LessonSeries>.Failure([.. errors]);

        return Result<LessonSeries>.Success(new LessonSeries(
            Guid.NewGuid(),
            tutorTelegramId,
            studentId,
            Normalize(title),
            startDate,
            endDate,
            weekdays,
            startTimeLocal,
            durationMinutes,
            timeZone,
            price,
            createdAtUtc));
    }

    /// <summary>Replaces the editable fields. Changing the weekdays/time means cancel + recreate.</summary>
    public Result UpdateDetails(string? title, DateOnly? endDate, decimal? price)
    {
        var errors = new List<Error>();
        if (ValidateEndDate(endDate, StartDate) is { } endDateError)
            errors.Add(endDateError);
        if (ValidatePrice(price) is { } priceError)
            errors.Add(priceError);
        if (errors.Count > 0)
            return Result.Failure([.. errors]);

        Title = Normalize(title);
        EndDate = endDate;
        Price = price;
        return Result.Success();
    }

    /// <summary>
    /// Ends the series: no occurrences happen after <paramref name="lastDate"/>. Ending before
    /// <see cref="StartDate"/> deactivates the series entirely (it never produced an occurrence);
    /// otherwise the existing <see cref="EndDate"/> is only ever tightened, never extended.
    /// Physical lessons are untouched — future virtual slots simply stop expanding.
    /// </summary>
    public void End(DateOnly lastDate)
    {
        if (lastDate < StartDate)
        {
            IsActive = false;
            return;
        }

        if (EndDate is null || EndDate > lastDate)
            EndDate = lastDate;
    }

    /// <summary>Ends the series as of "today" in its own time zone — see <see cref="End"/>.</summary>
    public void EndAsOf(DateTimeOffset nowUtc) =>
        End(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, TimeZone).DateTime));

    /// <summary>
    /// Moves the series to another time zone keeping the wall-clock schedule: a 16:00 lesson
    /// stays at 16:00, now in <paramref name="timeZone"/> — the UTC instants of future
    /// occurrences shift accordingly. Used when the tutor's profile zone changes.
    /// </summary>
    public void MoveToTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
    }

    /// <summary>
    /// Computes the concrete occurrences whose local date falls within
    /// <c>[fromLocal, toLocal]</c> (both inclusive), clipped to the series' own
    /// <c>[StartDate, EndDate]</c> window: every date whose weekday is in <see cref="Weekdays"/>.
    /// Local wall-clock time is converted to UTC per occurrence, so DST is handled: a 16:00
    /// lesson stays at 16:00 local. A start that falls into the spring-forward gap (an invalid
    /// local time) is shifted one hour later; for the ambiguous fall-back hour
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> picks the standard-time offset.
    /// </summary>
    public IReadOnlyList<LessonOccurrence> GetOccurrences(DateOnly fromLocal, DateOnly toLocal)
    {
        var first = fromLocal > StartDate ? fromLocal : StartDate;
        var last = EndDate is { } end && end < toLocal ? end : toLocal;

        var occurrences = new List<LessonOccurrence>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (!Weekdays.Contains(date.DayOfWeek))
                continue;

            var local = date.ToDateTime(StartTimeLocal, DateTimeKind.Unspecified);
            if (TimeZone.IsInvalidTime(local))
                local = local.AddHours(1);

            var startUtc = new DateTimeOffset(local, TimeZone.GetUtcOffset(local)).ToUniversalTime();
            occurrences.Add(new LessonOccurrence(date, startUtc, startUtc.AddMinutes(DurationMinutes)));
        }

        return occurrences;
    }

    private static Error? ValidateEndDate(DateOnly? endDate, DateOnly startDate) =>
        endDate is { } end && end < startDate
            ? new Error(
                "LessonSeries.EndDateBeforeStartDate", "End date must not precede start date.", "EndDate")
            : null;

    private static Error? ValidatePrice(decimal? price) =>
        price is < 0
            ? new Error("LessonSeries.NegativePrice", "Price must be zero or positive.", "Price")
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A single computed occurrence of a <see cref="LessonSeries"/>.</summary>
public readonly record struct LessonOccurrence(
    DateOnly OccurrenceDate,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);
