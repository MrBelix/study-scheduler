namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Persistence contract for <see cref="LessonSeries"/>. Every method reads the CURRENT TENANT's
/// series — except the one that says otherwise in its name.
/// </summary>
public interface ILessonSeriesRepository
{
    Task<LessonSeries?> GetByIdAsync(Guid id, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// The series that can still produce occurrences: open-ended, or ending on/after
    /// <paramref name="notEndedBefore"/>. A null cut-off returns every series (no date filter).
    /// Untracked — read-only consumers (expansion, overlap checks).
    /// </summary>
    Task<IReadOnlyList<LessonSeries>> GetActiveAsync(
        DateOnly? notEndedBefore = null,
        CancellationToken ct = default);

    /// <summary>All of the tutor's series (active and ended), oldest first — for the series list.</summary>
    Task<IReadOnlyList<LessonSeries>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// EVERY tutor's series that had already started by <paramref name="startedOnOrBefore"/> and can
    /// still produce occurrences on/after <paramref name="notEndedBefore"/>, oldest first. The one
    /// deliberately un-scoped query over series: its only caller is background maintenance, which runs
    /// without a user context and must see all tenants — hence the name it has to be called by.
    /// Untracked.
    /// </summary>
    Task<IReadOnlyList<LessonSeries>> GetStartedNotEndedAcrossAllTutorsAsync(
        DateOnly startedOnOrBefore,
        DateOnly notEndedBefore,
        CancellationToken ct = default);

    /// <summary>
    /// Stages the series for insertion. Ownership is not set here: the scope's tutor is stamped onto
    /// it when the unit of work commits.
    /// </summary>
    void Add(LessonSeries series);

    void Update(LessonSeries series);
}
