using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// A series write that also swept lessons away: the series as it now stands, plus the future rows
/// that were removed because the schedule they belonged to no longer covers them. The list is empty
/// whenever nothing ahead was lost.
/// </summary>
public sealed record SeriesChange(LessonSeries Series, IReadOnlyList<Lesson> RemovedLessons);

/// <summary>
/// Outcome of <see cref="LessonService.UpdateSeriesAsync"/> — a closed set the endpoint maps onto its
/// HTTP results. Free of ASP.NET types, so the orchestration stays testable without the HTTP stack.
/// </summary>
public abstract record SeriesUpdateOutcome
{
    private SeriesUpdateOutcome() { }

    /// <summary>The series was updated and committed.</summary>
    public sealed record Ok(SeriesChange Change) : SeriesUpdateOutcome;

    /// <summary>The request contradicts itself or a domain invariant; nothing was saved.</summary>
    public sealed record Validation(Result Failure) : SeriesUpdateOutcome;

    /// <summary>The window the change would newly expose is taken; nothing was saved.</summary>
    public sealed record Conflict(IReadOnlyList<LessonConflict> Conflicts) : SeriesUpdateOutcome;
}

/// <summary>
/// The series half of <see cref="LessonService"/> — the same class and the same injected instance,
/// split off for readability: creating the weekly schedule behind the create form's <c>Repeat</c>,
/// editing that schedule, and ending it effective immediately.
/// A series is a generation rule, so every write here follows the same discipline: check the window
/// the change exposes, mutate the rule, sweep the future rows it no longer accounts for, and let the
/// generator refill the horizon.
/// </summary>
public sealed partial class LessonService
{
    /// <summary>
    /// The repeat branch of the one create form: a weekly series anchored in the tutor's profile time
    /// zone, starting on the requested date, plus its first batch of physical lessons — everything up
    /// to <see cref="PlanningHorizon"/>, written in this very request so the tutor sees the schedule
    /// filled the moment they submit it. An end date beyond the horizon is perfectly legal: the
    /// batch stops at the horizon and the nightly extender rolls the window on from there.
    /// 409 material when the weekly slot collides with anything already booked.
    /// </summary>
    private async Task<CreateLessonOutcome> CreateSeriesAsync(
        CreateLessonRequest request,
        LessonRepeatRequest repeat,
        TimeZoneInfo zone,
        CancellationToken ct)
    {
        var pattern = WeeklyPattern.Create(repeat.Weekdays, request.StartTimeLocal, request.DurationMinutes, zone);
        if (!pattern.IsSuccess)
            return new CreateLessonOutcome.Validation(pattern);

        var created = LessonSeries.Create(
            request.StudentId, pattern.Value, request.Date, clock.GetUtcNow(),
            repeat.Title, repeat.EndDate, request.Price);
        if (!created.IsSuccess)
            return new CreateLessonOutcome.Validation(created);
        var series = created.Value;

        var conflicts = await overlapChecker.CheckSeriesAsync(series, ct: ct);
        if (conflicts.Count > 0)
            return new CreateLessonOutcome.Conflict(conflicts);

        seriesRepo.Add(series);
        // Committed on its own first: the lessons carry a foreign key to it, and the generator's
        // existence check must read a series that already exists. The commit is also where the series
        // gets its owner, so the rows generated below are written into the same tenant.
        await uow.SaveChangesAsync(ct);

        var generated = await generator.GenerateAsync(
            series, series.StartDate, PlanningHorizon.LastDateFrom(series.StartDate), ct);
        if (generated.Count > 0)
            await uow.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created series {SeriesId} for tutor {TutorId} starting {StartDate} with {Generated} lesson(s)",
            series.Id, series.TutorTelegramId, series.StartDate, generated.Count);
        return new CreateLessonOutcome.SeriesCreated(series);
    }

    /// <summary>
    /// Edits a series in full: its name and price, the weekly schedule itself (weekdays, start time,
    /// duration) and the date it runs until. A series is a generation rule rather than a projection,
    /// so a schedule change is applied by REWRITING what it already generated: the future rows the new
    /// rule no longer accounts for are deleted and the generator refills
    /// <c>[today, min(today + horizon, endDate)]</c> around whatever is left standing.
    /// <c>KeepCustomized</c> (the default) spares the occurrences somebody edited by hand; sending it
    /// as <c>false</c> replaces them too. Lessons that already started are never touched, and neither
    /// is a completed one wherever it sits — it happened and carries its money.
    /// A new price always reaches the whole future: the rows a new weekly pattern regenerates carry it
    /// from the generator, and every row the edit leaves standing is repriced in place, so no lesson
    /// moves for a price change alone and none is left behind on the old one.
    /// The window the change newly exposes is conflict-checked before anything is written; the
    /// reported removals are the genuine losses only, not the rows the refill re-created.
    /// A series of a student the tutor stopped teaching is not editable at all — see
    /// <see cref="StopTeachingAsync"/>.
    /// Null when the id addresses no series of this tutor.
    /// </summary>
    public async Task<SeriesUpdateOutcome?> UpdateSeriesAsync(
        Guid seriesId,
        UpdateLessonSeriesRequest request,
        CancellationToken ct = default)
    {
        var series = await seriesRepo.GetByIdAsync(seriesId, track: true, ct);
        if (series is null)
            return null;

        // Archiving said the tutor stopped teaching them, and the cascade behind it ended this very
        // rule. Re-opening it — a later end date, or clearing it altogether — would refill their
        // schedule on the spot and keep the nightly extender filling it every night after, with no
        // second archiving to undo that. So the edit is refused as a whole, exactly as booking is:
        // whatever the field, it is a schedule for someone nobody teaches any more.
        // One tenant-scoped read — the student is this tutor's own by construction.
        if (await students.GetByIdAsync(series.StudentId, ct: ct) is { Status: StudentStatus.Archived })
            return new SeriesUpdateOutcome.Validation(Result.Failure(new Error(
                "Student.Archived",
                "This student is archived. Restore them before changing their schedule.",
                "StudentId")));

        var endDate = ResolveEndDate(request.EndDate, request.ClearEndDate, series.EndDate);
        if (!endDate.IsSuccess)
            return new SeriesUpdateOutcome.Validation(endDate);

        var pattern = ResolvePattern(series.Pattern, request);
        if (!pattern.IsSuccess)
            return new SeriesUpdateOutcome.Validation(pattern);

        var title = request.Title ?? series.Title;
        var price = request.Price ?? series.Price;

        // Runs exactly the invariants Update would, against the stored start date, so an unhonourable
        // edit is refused before the rule — or a single row of it — is touched.
        var candidate = LessonSeries.Create(
            series.StudentId, pattern.Value, series.StartDate, series.CreatedAtUtc,
            title, endDate.Value, price);
        if (!candidate.IsSuccess)
            return new SeriesUpdateOutcome.Validation(candidate);

        var now = clock.GetUtcNow();
        var today = pattern.Value.LocalDateOf(now);
        var patternChanged = !pattern.Value.Equals(series.Pattern);
        var scheduleChanged = patternChanged || endDate.Value != series.EndDate;
        // A new weekly pattern re-places every future row, so those come back from the generator
        // carrying the new price by themselves. Anything short of that leaves rows standing — a moved
        // end date only sweeps what falls outside the new window, and a name-or-price edit sweeps
        // nothing at all — and those rows are the ones a price change has to be carried to, or the
        // tutor would be left with an old-priced middle and a new-priced tail.
        var repriceKeptRows = !patternChanged && request.Price is { } asked && asked != series.Price;

        // Checked before anything is mutated, and only over the window the change newly exposes: the
        // series must never be reported against the occurrences (or the rows) it already owns —
        // including the completed lessons a previous tightening spared inside that very window, which
        // would otherwise make a tightening impossible to undo.
        if (ChangeProbe(series, pattern.Value, endDate.Value, price, today, patternChanged) is { } probe)
        {
            var conflicts = await overlapChecker.CheckSeriesAsync(
                probe, new ReplacedSeries(seriesId, probe.StartDate), ct);
            if (conflicts.Count > 0)
                return new SeriesUpdateOutcome.Conflict(conflicts);
        }

        // The candidate above validated these very inputs, so this cannot fail — but the rule, not
        // this method, stays the one that says so.
        var updated = series.Update(title, pattern.Value, endDate.Value, price);
        if (!updated.IsSuccess)
            return new SeriesUpdateOutcome.Validation(updated);

        List<Lesson> deleted = scheduleChanged
            ? await SweepFutureAsync(series, now, patternChanged, request.KeepCustomized, ct)
            : [];
        // The rows the edit leaves standing are repriced where they are, instead of being churned for
        // a change that never moved them. The swept ones are handed over so they stay swept.
        if (repriceKeptRows)
            await RepriceFutureAsync(seriesId, now, request.Price!.Value, deleted, ct);

        seriesRepo.Update(series);
        // One commit for the rule and everything the sweep (or the repricing) did to its rows. The
        // refill has to follow it: the generator asks the database which dates are taken, so the rows
        // being deleted must be gone by then — and deleting a slot and re-inserting the same
        // (SeriesId, OccurrenceDate) in a single save would race its own unique index.
        await uow.SaveChangesAsync(ct);

        IReadOnlyList<Lesson> generated = [];
        if (scheduleChanged)
        {
            generated = await generator.GenerateAsync(series, today, PlanningHorizon.LastDateFrom(today), ct);
            if (generated.Count > 0)
                await uow.SaveChangesAsync(ct);
        }

        // Only genuine losses are reported: a date the refill covered again still holds its lesson,
        // and saying otherwise would have the client announce cancellations that never happened.
        var refilled = generated.Select(l => l.OccurrenceDate!.Value).ToHashSet();
        var removed = deleted.Where(l => !refilled.Contains(l.OccurrenceDate!.Value)).ToList();

        logger.LogInformation(
            "Updated series {SeriesId} of tutor {TutorId}; end date {EndDate}, {DeletedCount} future lesson(s) swept, {GeneratedCount} generated, {RemovedCount} reported removed",
            seriesId, series.TutorTelegramId, series.EndDate, deleted.Count, generated.Count, removed.Count);
        return new SeriesUpdateOutcome.Ok(new SeriesChange(series, removed));
    }

    /// <summary>
    /// Cancels a series effective immediately (in its own time zone): its last possible lesson day
    /// becomes yesterday, so it produces nothing from today on.
    /// Every lesson ahead is swept away with the schedule it belonged to and reported so the client
    /// can notify the student — except a completed one, which already happened and carries its money,
    /// and, unless <c>KeepCustomized</c> is sent as <c>false</c>, the occurrences somebody edited by
    /// hand: those are per-lesson decisions rather than plan. Lessons that already started (today's
    /// and the past's) are untouched — the sweep selects by instant, so today's occurrence survives as
    /// the row it has always been. Null when the id addresses no series of this tutor.
    /// </summary>
    public async Task<SeriesChange?> CancelSeriesAsync(
        Guid seriesId,
        CancelLessonSeriesRequest request,
        CancellationToken ct = default)
    {
        var series = await seriesRepo.GetByIdAsync(seriesId, track: true, ct);
        if (series is null)
            return null;

        var now = clock.GetUtcNow();

        series.CancelAsOf(now);

        // The schedule is gone, so nothing still ahead of us belongs to it.
        var removed = await SweepFutureAsync(series, now, wholeFuture: true, request.KeepCustomized, ct);

        seriesRepo.Update(series);
        await uow.SaveChangesAsync(ct);

        logger.LogInformation(
            "Cancelled series {SeriesId} of tutor {TutorId} as of {Today}; {RemovedCount} future lesson(s) removed",
            seriesId, series.TutorTelegramId, series.Pattern.LocalDateOf(now), removed.Count);
        return new SeriesChange(series, removed);
    }

    /// <summary>
    /// The end date a request means: the one it sent, <c>null</c> when it explicitly asks to clear it
    /// (open-ended), or the current one when the field is absent. A null field means "not provided"
    /// throughout this API, so clearing needs its own flag — and asking for both at once is a
    /// contradiction rather than a silent winner.
    /// </summary>
    private static Result<DateOnly?> ResolveEndDate(DateOnly? requested, bool clear, DateOnly? current)
    {
        if (!clear)
            return Result<DateOnly?>.Success(requested ?? current);

        return requested is null
            ? Result<DateOnly?>.Success(null)
            : Result<DateOnly?>.Failure(new Error(
                "LessonSeries.AmbiguousEndDate",
                "Send either 'endDate' or 'clearEndDate', not both.",
                "EndDate"));
    }

    /// <summary>
    /// The weekly pattern a request means: the current one when it carries no schedule field at all
    /// (returned as-is, so "unchanged" is decidable by identity), otherwise a new one built field by
    /// field over the current values. The time zone is not editable — it is the one the series was
    /// created in, and every date of the series is expressed in it.
    /// </summary>
    private static Result<WeeklyPattern> ResolvePattern(WeeklyPattern current, UpdateLessonSeriesRequest request)
    {
        if (request.Weekdays is null && request.StartTimeLocal is null && request.DurationMinutes is null)
            return Result<WeeklyPattern>.Success(current);

        return WeeklyPattern.Create(
            request.Weekdays ?? current.Days,
            request.StartTimeLocal ?? current.StartTimeLocal,
            request.DurationMinutes ?? current.DurationMinutes,
            current.TimeZone);
    }

    /// <summary>
    /// A stand-in series covering only the window the change newly exposes, or null when it exposes
    /// nothing. A new pattern re-places every occurrence from today on, so the whole remaining window
    /// is fresh ground; an end date that only moves outward exposes <c>[oldEnd + 1, newEnd]</c> and
    /// nothing more, while a tightening, a no-op or an already open-ended series expose nothing at
    /// all. Checking the probe instead of the series keeps the already-accepted part of the schedule —
    /// and every row generated from it — out of the conflict check.
    /// </summary>
    private static LessonSeries? ChangeProbe(
        LessonSeries series,
        WeeklyPattern pattern,
        DateOnly? newEndDate,
        decimal? price,
        DateOnly today,
        bool patternChanged)
    {
        DateOnly from;
        if (patternChanged)
        {
            // The past keeps the rows the previous schedule generated for it; only the future moves.
            from = today > series.StartDate ? today : series.StartDate;
        }
        else
        {
            if (series.EndDate is not { } currentEnd)
                return null;
            if (newEndDate is { } newEnd && newEnd <= currentEnd)
                return null;

            var next = currentEnd.AddDays(1);
            from = next > series.StartDate ? next : series.StartDate;
        }

        if (newEndDate is { } end && end < from)
            return null;

        // Same student, pattern and price as the edited series — only the window differs, and both of
        // its bounds are checked above, so the factory cannot fail here. The probe is never saved, so
        // it needs no owner: the reads it drives are scoped to the tenant already.
        return LessonSeries.Create(
            series.StudentId, pattern, from, series.CreatedAtUtc,
            series.Title, newEndDate, price).Value;
    }

    /// <summary>
    /// Deletes the lessons a schedule change invalidates and returns them. Only rows that have not
    /// started yet are in scope, and a completed one never is whatever its date: it happened and
    /// carries its money, so it is history rather than plan. Of the rest,
    /// <paramref name="wholeFuture"/> — a new weekly pattern re-places every occurrence — takes all of
    /// them, while a changed window takes only those <paramref name="series"/> no longer has a slot
    /// for; and <paramref name="keepCustomized"/> spares the occurrences somebody edited by hand.
    /// Staged only: the caller commits.
    /// </summary>
    private async Task<List<Lesson>> SweepFutureAsync(
        LessonSeries series,
        DateTimeOffset now,
        bool wholeFuture,
        bool keepCustomized,
        CancellationToken ct)
    {
        var future = await lessons.GetFutureForSeriesAsync(series.Id, now, track: true, ct);

        var doomed = future
            .Where(l => l.Status != LessonStatus.Completed
                && (!keepCustomized || !l.IsCustomized)
                && (wholeFuture || l.OccurrenceDate is not { } date || !series.HasSlotOn(date)))
            .ToList();

        foreach (var lesson in doomed)
            lessons.Remove(lesson);

        return doomed;
    }

    /// <summary>
    /// Applies a new series price to the lessons ahead nobody has touched. Rows are written months in
    /// advance now, so the snapshot the generator took has to follow the rule it came from — otherwise
    /// raising the price would mean re-pricing a whole horizon by hand. Customized occurrences keep
    /// the price they were given, completed ones keep the money they earned, and lessons that already
    /// started are never rewritten. The rows <paramref name="swept"/> holds are skipped as well: their
    /// deletion is staged but not committed, so the query still hands them back and rewriting one
    /// would resurrect it. Staged only: the caller commits.
    /// </summary>
    private async Task<int> RepriceFutureAsync(
        Guid seriesId,
        DateTimeOffset now,
        decimal price,
        IReadOnlyCollection<Lesson> swept,
        CancellationToken ct)
    {
        var future = await lessons.GetFutureForSeriesAsync(seriesId, now, track: true, ct);
        var doomed = swept.Select(l => l.Id).ToHashSet();

        var repriced = 0;
        foreach (var lesson in future)
        {
            if (doomed.Contains(lesson.Id)
                || lesson.IsCustomized
                || lesson.Status == LessonStatus.Completed
                || lesson.Price == price)
                continue;

            // Non-negative: the candidate series validated this very value before anything moved.
            lesson.SetPrice(price);
            lessons.Update(lesson);
            repriced++;
        }

        return repriced;
    }
}
