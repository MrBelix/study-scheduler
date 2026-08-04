namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Persistence contract for <see cref="Lesson"/>. Lives in the domain so the API depends on the
/// abstraction; infrastructure (EF Core) provides the implementation. Every method here reads the
/// CURRENT TENANT's lessons and nothing else — ownership is enforced by persistence rather than
/// passed in, so a cross-tenant id reads exactly like a missing one.
/// </summary>
public interface ILessonRepository
{
    /// <summary>The lesson, or null when it is missing or another tutor's. Untracked unless <paramref name="track"/>.</summary>
    Task<Lesson?> GetByIdAsync(Guid id, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// The lessons the given ids address, in one round trip. An id that addresses nothing of this
    /// tutor's is simply absent from the result — which is how a caller learns it could not be
    /// resolved. Untracked unless <paramref name="track"/>.
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        bool track = false,
        CancellationToken ct = default);

    /// <summary>
    /// Lessons intersecting <c>[fromUtc, toUtc)</c>, ordered by start. Includes every status (a
    /// cancelled lesson still shows on the schedule).
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetInRangeAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Non-cancelled lessons overlapping <c>(startUtc, endUtc)</c> — strict inequalities, so
    /// back-to-back lessons do not conflict.
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetOverlappingAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        CancellationToken ct = default);

    /// <summary>Non-cancelled lessons starting at or after <paramref name="fromUtc"/>.</summary>
    Task<IReadOnlyList<Lesson>> GetFromDateAsync(
        DateTimeOffset fromUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Slots that already hold a lesson within the local date range, for many series in one round
    /// trip — the existence check generation skips taken dates by, without a query per date.
    /// </summary>
    Task<IReadOnlyList<SeriesSlot>> GetMaterializedSlotsAsync(
        IReadOnlyCollection<Guid> seriesIds,
        DateOnly fromLocal,
        DateOnly toLocal,
        CancellationToken ct = default);

    /// <summary>
    /// Lifetime totals over one student's lessons, aggregated by the database — the details screen
    /// never loads a whole history just to count it.
    /// </summary>
    Task<StudentLessonStats> GetStudentStatsAsync(
        Guid studentId,
        CancellationToken ct = default);

    /// <summary>
    /// What one student owes: their unpaid completed lessons over the whole history — exactly the
    /// rows <see cref="Lesson.IsDebt"/> defines and the reports' debtor ledger counts — newest first.
    /// Deliberately not range-bounded, and deliberately blind to the student's status: a debt does not
    /// stop being owed because the reporting period moved on, or because the tutor stopped teaching
    /// them. Read-only (untracked).
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetDebtForStudentAsync(
        Guid studentId,
        CancellationToken ct = default);

    /// <summary>
    /// Rows of the series that have not started yet (<c>StartUtc &gt; afterUtc</c>) — the part of the
    /// schedule a series edit may still rewrite. Selected by instant rather than by occurrence date,
    /// so a lesson that already happened is out of reach whatever slot it was generated for.
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetFutureForSeriesAsync(
        Guid seriesId,
        DateTimeOffset afterUtc,
        bool track = false,
        CancellationToken ct = default);

    /// <summary>
    /// Rows of one student that have not started yet (<c>StartUtc &gt; afterUtc</c>), one-offs and
    /// series occurrences alike — the part of their schedule that still lies ahead, which is what
    /// archiving them sweeps away. Selected by instant, so a lesson already under way is out of reach.
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetFutureForStudentAsync(
        Guid studentId,
        DateTimeOffset afterUtc,
        bool track = false,
        CancellationToken ct = default);

    /// <summary>
    /// Stages the lesson for insertion. Ownership is not set here: the scope's tutor is stamped onto
    /// it when the unit of work commits.
    /// </summary>
    void Add(Lesson lesson);

    void Update(Lesson lesson);

    void Remove(Lesson lesson);
}
