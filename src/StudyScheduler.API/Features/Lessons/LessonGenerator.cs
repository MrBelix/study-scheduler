using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Turns a <see cref="LessonSeries"/> — a generation rule — into the physical <see cref="Lesson"/>
/// rows that fill the <see cref="PlanningHorizon"/>. Two entry points over one primitive:
/// <see cref="GenerateAsync"/> fills one series' window (the synchronous first batch a create request
/// runs, and the refill a series edit runs), and <see cref="ExtendAllAsync"/> walks every active
/// series once a night to roll the window forward — which doubles as the backfill for series that
/// predate eager generation.
/// Idempotent by construction: a date that already has ANY row is skipped, so re-running writes
/// nothing and a customized or cancelled occurrence is never regenerated over. Existence is resolved
/// with a single bulk query per series, never one per date.
/// Nothing here commits; the caller owns the unit of work.
/// </summary>
public sealed class LessonGenerator(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo,
    IStudentRepository students,
    IUnitOfWork uow,
    ITutorScope tenant,
    TimeProvider clock,
    ILogger<LessonGenerator> logger)
{
    /// <summary>
    /// Stages a row for every occurrence of <paramref name="series"/> in
    /// <c>[fromLocal, toLocal]</c> that has none yet, and returns the rows it staged. The window is
    /// clipped to the series' own <c>[StartDate, EndDate]</c>, so passing a horizon that runs past the
    /// end date simply stops at the end date. Price is snapshotted from the series (or the student's
    /// rate) and duration comes from the occurrence itself.
    /// The reads and the rows staged here belong to whoever the scope's tenant is — the caller's own
    /// tutor for a request, and the series' owner for the nightly pass (see
    /// <see cref="ExtendAllAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<Lesson>> GenerateAsync(
        LessonSeries series,
        DateOnly fromLocal,
        DateOnly toLocal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(series);

        var occurrences = series.GetOccurrences(fromLocal, toLocal);
        if (occurrences.Count == 0)
            return [];

        // One bulk existence check for the whole window: a date with ANY row — generated, customized
        // or cancelled — belongs to that row and is left alone.
        var taken = (await lessons.GetMaterializedSlotsAsync([series.Id], fromLocal, toLocal, ct))
            .Select(s => s.OccurrenceDate)
            .ToHashSet();

        // Resolved at most once per call, and only when there is actually something to write.
        decimal? price = null;
        var staged = new List<Lesson>();
        foreach (var occurrence in occurrences)
        {
            if (taken.Contains(occurrence.OccurrenceDate))
                continue;

            price ??= await ResolvePriceAsync(series, ct);
            var lesson = BuildLesson(series, occurrence, price.Value);
            lessons.Add(lesson);
            staged.Add(lesson);
        }

        return staged;
    }

    /// <summary>
    /// One nightly pass over EVERY tenant's still-active series: fills each one's rolling window from
    /// today (in the series' own zone) to the horizon, committing per series so a single bad series
    /// costs only its own rows. Returns how many lessons the pass created.
    /// The pass has no tenant of its own, so it is a sequence of per-tenant passes: the candidate
    /// series are read across all tutors, then each one's owner becomes the scope's tutor before its
    /// window is filled — which is what makes the reads inside <see cref="GenerateAsync"/> (existing
    /// slots, the student behind the series) see that tutor's rows and no one else's, and what gives
    /// the rows it writes their owner.
    /// </summary>
    public async Task<int> ExtendAllAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // The candidate filter is a coarse pre-selection in UTC days; each series' real window is
        // computed below in its own zone, which can differ from UTC by at most a day either way.
        var utcToday = DateOnly.FromDateTime(now.UtcDateTime);
        var candidates = await seriesRepo.GetStartedNotEndedAcrossAllTutorsAsync(
            startedOnOrBefore: PlanningHorizon.LastDateFrom(utcToday.AddDays(1)),
            notEndedBefore: utcToday.AddDays(-1),
            ct);
        if (candidates.Count == 0)
            return 0;

        var generated = 0;
        var failed = 0;
        foreach (var series in candidates)
        {
            // Every read and write below belongs to this series' owner: the pass has no tenant of
            // its own, so it borrows each series' as it goes — a violation is the pass' problem,
            // not the series', so it is deliberately outside the per-series catch.
            tenant.SetForBackground(series.TutorTelegramId);
            try
            {
                var today = series.Pattern.LocalDateOf(now);
                var staged = await GenerateAsync(series, today, PlanningHorizon.LastDateFrom(today), ct);
                if (staged.Count == 0)
                    continue;

                await uow.SaveChangesAsync(ct);
                generated += staged.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One series whose stored data cannot produce a lesson must not cost the rest of the
                // pass — and neither must the rows it left staged, which every later save in this
                // scope would replay and fail on. Drop them before moving on.
                uow.DiscardChanges();
                failed++;
                logger.LogError(ex, "Generating lessons for series {SeriesId} failed", series.Id);
            }
        }

        logger.LogInformation(
            "Lesson generation pass created {Generated} lesson(s) across {SeriesCount} active series ({Failed} failed)",
            generated, candidates.Count, failed);

        return generated;
    }

    /// <summary>
    /// Instantiates (without staging) the row for one occurrence, carrying the series link, the
    /// canonical occurrence date and a price snapshot. Duration comes from the occurrence itself, so a
    /// slot is always written at the exact wall clock it was expanded at — DST included.
    /// </summary>
    private Lesson BuildLesson(LessonSeries series, LessonOccurrence occurrence, decimal price)
    {
        var durationMinutes = (int)(occurrence.EndUtc - occurrence.StartUtc).TotalMinutes;

        var created = Lesson.Create(
            series.StudentId,
            occurrence.StartUtc,
            durationMinutes,
            price,
            clock.GetUtcNow(),
            seriesId: series.Id,
            occurrenceDate: occurrence.OccurrenceDate);
        if (created.IsSuccess)
            return created.Value;

        // The inputs come from a persisted series, not the user — a failure means the stored data
        // violates lesson invariants. Surface it as the data anomaly it is.
        var details = string.Join("; ", created.Errors.Select(e => e.Message));
        logger.LogError(
            "Generating slot {OccurrenceDate} of series {SeriesId} produced an invalid lesson: {Errors}",
            occurrence.OccurrenceDate, series.Id, details);
        throw new InvalidOperationException(
            $"Series {series.Id} slot {occurrence.OccurrenceDate:yyyy-MM-dd} cannot be generated: {details}");
    }

    /// <summary>Price snapshot: the series' own price, or the student's current rate (0 if gone).</summary>
    private async Task<decimal> ResolvePriceAsync(LessonSeries series, CancellationToken ct)
    {
        if (series.Price is { } price)
            return price;

        var student = await students.GetByIdAsync(series.StudentId, ct: ct);
        if (student is not null)
            return student.Rate;

        // Data anomaly guard: a series whose student is gone must not fail the whole generation pass —
        // snapshot a zero price instead.
        logger.LogWarning(
            "Student {StudentId} behind series {SeriesId} not found; generating with price 0",
            series.StudentId, series.Id);
        return 0m;
    }
}
