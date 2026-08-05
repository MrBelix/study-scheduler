using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Outcome of <see cref="LessonService.CreateAsync"/> — a closed set the endpoint maps onto its HTTP
/// results. Exactly which of the two "created" cases arrives is what the one create form decides.
/// Free of ASP.NET types, so the branching stays testable without the HTTP stack.
/// </summary>
public abstract record CreateLessonOutcome
{
    private CreateLessonOutcome() { }

    /// <summary>The no-repeat branch: one physical lesson was written.</summary>
    public sealed record LessonCreated(Lesson Lesson) : CreateLessonOutcome;

    /// <summary>The repeat branch: a weekly series was written, together with its first batch of lessons.</summary>
    public sealed record SeriesCreated(LessonSeries Series) : CreateLessonOutcome;

    /// <summary>The request cannot be honoured as sent; nothing was saved.</summary>
    public sealed record Validation(Result Failure) : CreateLessonOutcome;

    /// <summary>The requested time collides with existing lessons or series; nothing was saved.</summary>
    public sealed record Conflict(IReadOnlyList<LessonConflict> Conflicts) : CreateLessonOutcome;
}

/// <summary>
/// Outcome of <see cref="LessonService.UpdateAsync"/> — a closed set both callers of the patch seam
/// map onto their own answers: the endpoint onto its HTTP results, the bot's webhook onto its
/// callback toast. Free of ASP.NET types so the pipeline stays testable without the HTTP stack.
/// </summary>
public abstract record LessonPatchOutcome
{
    private LessonPatchOutcome() { }

    /// <summary>The patch was applied and committed.</summary>
    public sealed record Ok(Lesson Lesson) : LessonPatchOutcome;

    /// <summary>One or more fields failed domain validation; nothing was saved.</summary>
    public sealed record Validation(Result Failure) : LessonPatchOutcome;

    /// <summary>The new time collides with existing lessons or series; nothing was saved.</summary>
    public sealed record Conflict(IReadOnlyList<LessonConflict> Conflicts) : LessonPatchOutcome;
}

/// <summary>
/// A student's next upcoming lesson: when it starts, how long it runs and what it is about — plus
/// the id of the row it is. A lesson generated from a series is a row like any other, so this is the
/// very identity <c>GET /lessons</c> hands out.
/// </summary>
public sealed record UpcomingLesson(
    DateTimeOffset StartUtc,
    int DurationMinutes,
    string? Subject,
    Guid LessonId)
{
    /// <summary>
    /// One row as the students screens show it. <paramref name="seriesTitle"/> names the schedule the
    /// row was generated from and stands in for a topic nobody has written yet.
    /// </summary>
    public static UpcomingLesson From(Lesson lesson, string? seriesTitle = null) => new(
        lesson.StartUtc,
        lesson.DurationMinutes,
        lesson.Topic ?? seriesTitle,
        lesson.Id);
}

/// <summary>
/// One lesson a student still owes for: taught, never paid. Same shape and the same subject rule as
/// <see cref="UpcomingLesson"/> — the topic somebody wrote, or the title of the series the row was
/// generated from — plus the price that is actually owed, and the id the settle route takes.
/// </summary>
public sealed record UnpaidLesson(
    DateTimeOffset StartUtc,
    int DurationMinutes,
    decimal Price,
    string? Subject,
    Guid LessonId)
{
    public static UnpaidLesson From(Lesson lesson, string? seriesTitle = null) => new(
        lesson.StartUtc,
        lesson.DurationMinutes,
        lesson.Price,
        lesson.Topic ?? seriesTitle,
        lesson.Id);
}

/// <summary>
/// What a student owes in total, for the banner on their page: the money and how many lessons it
/// came from. Never a zero — a student who owes nothing has no debt at all (null), so the client
/// shows the banner exactly when it arrives.
/// </summary>
public sealed record StudentDebtSummary(decimal Amount, int LessonsCount);

/// <summary>
/// Everything the lesson routes do beyond speaking HTTP — single lessons and weekly series alike:
/// reading the row an id addresses, patching it, creating what the one create form asked for, and
/// editing or ending a series. The endpoints only bind the request and render the outcome.
/// The Students feature reads through it too: "when is each student's next lesson?" and "what do they
/// still owe me?" are questions about lessons, so they are answered here rather than by readers of
/// its own.
/// Whose lessons these are is never an argument: every repository read below is already scoped to the
/// current tenant, and every insert is stamped with it.
/// One class, one injectable instance: the series half lives in <c>LessonService.Series.cs</c> for
/// readability only, and everything else here (generator, overlap checker) is its machinery rather
/// than a second façade.
/// </summary>
public sealed partial class LessonService(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo,
    IStudentRepository students,
    ITutorProfileRepository profiles,
    LessonGenerator generator,
    LessonOverlapChecker overlapChecker,
    IUnitOfWork uow,
    TimeProvider clock,
    ILogger<LessonService> logger)
{
    /// <summary>
    /// The lesson an id addresses, read-only. Null when nothing is addressable — an unknown id, or
    /// another tutor's lesson, which the tenant-scoped lookup cannot see either way.
    /// </summary>
    public Task<Lesson?> GetAsync(Guid id, CancellationToken ct = default) =>
        lessons.GetByIdAsync(id, track: false, ct);

    /// <summary>
    /// The next upcoming lesson per student id — the earliest still-<see cref="LessonStatus.Scheduled"/>
    /// row starting at or after now — for every student of the tutor, or just <paramref name="studentId"/>.
    /// Students with nothing upcoming are absent from the map: a lesson the tutor has already marked
    /// <see cref="LessonStatus.Completed"/> (which can happen before its slot elapses) is a settled fact,
    /// not something still ahead, and cancelled rows never qualify either. The whole tenant is answered
    /// in one pass, so the students list needs no per-student query; lessons exist physically months
    /// ahead, so this is a plain query over rows: nothing is expanded and nothing is written.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, UpcomingLesson>> GetNextLessonsAsync(
        Guid? studentId = null,
        CancellationToken ct = default)
    {
        var earliest = new Dictionary<Guid, Lesson>();
        foreach (var lesson in await lessons.GetFromDateAsync(clock.GetUtcNow(), ct))
        {
            if (lesson.Status != LessonStatus.Scheduled)
                continue;

            if (studentId is { } id && lesson.StudentId != id)
                continue;

            if (!earliest.TryGetValue(lesson.StudentId, out var current) || lesson.StartUtc < current.StartUtc)
                earliest[lesson.StudentId] = lesson;
        }

        if (earliest.Count == 0)
            return new Dictionary<Guid, UpcomingLesson>();

        var titles = await ResolveSeriesTitlesAsync(earliest.Values, ct);
        return earliest.ToDictionary(
            e => e.Key,
            e => UpcomingLesson.From(e.Value, Title(titles, e.Value)));
    }

    /// <summary>
    /// What the student still owes, or null when they owe nothing: the money and the number of
    /// lessons behind it, over their whole history. The rows counted are the domain's
    /// <see cref="Lesson.IsDebt"/> ones — the very rows the Money screen bills them for — so the
    /// banner on their page and the debtors list can never quote different amounts.
    /// An archived student answers like any other: the archive cascade keeps completed lessons
    /// precisely because the money they carry outlives the schedule.
    /// </summary>
    public async Task<StudentDebtSummary?> GetDebtAsync(Guid studentId, CancellationToken ct = default)
    {
        var unpaid = await lessons.GetDebtForStudentAsync(studentId, ct);

        return unpaid.Count == 0
            ? null
            : new StudentDebtSummary(unpaid.Sum(l => l.Price), unpaid.Count);
    }

    /// <summary>
    /// The lessons that debt is made of, newest first — what the tutor ticks off on the debts screen.
    /// Empty when nothing is owed. The same rows <see cref="GetDebtAsync"/> counts, so the list and
    /// the banner above it always add up.
    /// </summary>
    public async Task<IReadOnlyList<UnpaidLesson>> GetDebtLessonsAsync(
        Guid studentId,
        CancellationToken ct = default)
    {
        var unpaid = await lessons.GetDebtForStudentAsync(studentId, ct);
        if (unpaid.Count == 0)
            return [];

        var titles = await ResolveSeriesTitlesAsync(unpaid, ct);
        return unpaid.Select(l => UnpaidLesson.From(l, Title(titles, l))).ToList();
    }

    /// <summary>
    /// Titles of the series behind the chosen rows, in one query — and only when a row actually needs
    /// one: a generated lesson carries no topic of its own until somebody writes one, and the screens
    /// have always shown the schedule's name in its place.
    /// </summary>
    private async Task<Dictionary<Guid, string?>> ResolveSeriesTitlesAsync(
        IEnumerable<Lesson> chosen,
        CancellationToken ct)
    {
        if (!chosen.Any(l => l is { SeriesId: not null, Topic: null }))
            return [];

        return (await seriesRepo.GetAllAsync(ct))
            .ToDictionary(s => s.Id, s => s.Title);
    }

    private static string? Title(IReadOnlyDictionary<Guid, string?> titles, Lesson lesson) =>
        lesson.SeriesId is { } seriesId ? titles.GetValueOrDefault(seriesId) : null;

    /// <summary>
    /// Partially updates the lesson behind an id: read the row, then run the patch pipeline below.
    /// Null when the id addresses nothing.
    /// A reschedule past the <see cref="PlanningHorizon"/> is refused up front — the request is
    /// unhonourable whichever lesson it names.
    /// This is the ONE seam a lesson is mutated through on someone's behalf: the app's
    /// <c>PATCH /lessons/{id}</c> and the bot's follow-up buttons both come through here.
    /// </summary>
    public async Task<LessonPatchOutcome?> UpdateAsync(
        Guid id,
        UpdateLessonRequest request,
        CancellationToken ct = default)
    {
        if (request.StartUtc is { } startUtc && await BeyondHorizonAsync(startUtc, ct))
            return new LessonPatchOutcome.Validation(Result.Failure(PlanningHorizon.Exceeded("StartUtc")));

        var lesson = await lessons.GetByIdAsync(id, track: true, ct);
        return lesson is null ? null : await ApplyPatchAsync(lesson, request, ct);
    }

    /// <summary>
    /// The patch pipeline itself: validate via the domain mutators, check overlaps when the time
    /// actually changes (or the lesson is un-cancelled), stage, commit once. Series occurrences reach
    /// it as the persisted rows their series generated, so every patch here is a plain update of an
    /// existing row.
    /// Two patches are refused outright rather than field by field, both of them ways of putting a
    /// lesson back on an ARCHIVED student's schedule — see <see cref="StopTeachingAsync"/>.
    /// The lesson arrives already resolved, which means it was already read through the tenant's
    /// filter — there is nothing left to prove about whose it is.
    /// Being the one place a lesson is mutated on someone's behalf makes this the one place that
    /// latches <see cref="Lesson.IsCustomized"/> — both the app's PATCH and the bot's follow-up
    /// buttons come through <see cref="UpdateAsync"/>, and both state a per-lesson fact the generator
    /// must never regenerate away.
    /// </summary>
    private async Task<LessonPatchOutcome> ApplyPatchAsync(
        Lesson lesson,
        UpdateLessonRequest request,
        CancellationToken ct)
    {
        var startUtc = request.StartUtc ?? lesson.StartUtc;
        var durationMinutes = request.DurationMinutes ?? lesson.DurationMinutes;
        var status = request.Status ?? lesson.Status;

        // Overlap-check inputs are captured before the mutators run — un-cancelling in particular
        // compares against the lesson's pre-patch status.
        var timeChanged = startUtc != lesson.StartUtc || durationMinutes != lesson.DurationMinutes;
        var unCancelling = lesson.Status == LessonStatus.Cancelled && status != LessonStatus.Cancelled;

        // The patches that would hand an archived student a lesson to be taught again — exactly what
        // the archive cascade took away: un-settling the completed row still ahead (the only kind it
        // spares there), or dragging a row it left in the past into the future. Both are bookings in
        // all but name, so both are refused like one, while everything that schedules nothing —
        // paying, naming, correcting a past settle — stays open.
        if (ReopensSchedule(lesson, startUtc, status)
            && await students.GetByIdAsync(lesson.StudentId, ct: ct) is { Status: StudentStatus.Archived })
            return new LessonPatchOutcome.Validation(Result.Failure(new Error(
                "Student.Archived",
                "This student is archived. Restore them before putting a lesson back on their schedule.",
                startUtc != lesson.StartUtc ? "StartUtc" : "Status")));

        // Domain mutators validate each field; failures are collected so one 400 still reports
        // every offending field, returned before the overlap check and before any save.
        // The time mutators run against the STORED status, so re-timing a completed lesson is refused
        // even when the same request also corrects it back to Scheduled: undoing a settle is its own
        // decision, made in its own request.
        var errors = new List<Error>();
        if (startUtc != lesson.StartUtc)
            errors.AddRange(lesson.Reschedule(startUtc).Errors);
        if (request.DurationMinutes is { } newDuration && newDuration != lesson.DurationMinutes)
            errors.AddRange(lesson.ChangeDuration(newDuration).Errors);
        if (request.Status is { } newStatus)
            errors.AddRange(lesson.ChangeStatus(newStatus).Errors);
        if (request.Price is { } price)
            errors.AddRange(lesson.SetPrice(price).Errors);
        if (request.IsPaid is { } isPaid)
            errors.AddRange(lesson.SetPaid(isPaid).Errors);
        if (request.Topic is not null)
            errors.AddRange(lesson.UpdateTopic(request.Topic).Errors);
        if (request.Description is not null)
            errors.AddRange(lesson.UpdateDescription(request.Description).Errors);
        if (errors.Count > 0)
            return new LessonPatchOutcome.Validation(Result.Failure([.. errors]));

        if (status != LessonStatus.Cancelled && (timeChanged || unCancelling))
        {
            // A lesson never collides with itself: excludeLessonId takes its own row out of the
            // comparison.
            var conflicts = await overlapChecker.CheckLessonAsync(
                startUtc, startUtc.AddMinutes(durationMinutes), excludeLessonId: lesson.Id, ct: ct);
            if (conflicts.Count > 0)
                return new LessonPatchOutcome.Conflict(conflicts);
        }

        // The patch is going through, so the occurrence now carries a decision of its own. Only
        // series-linked rows need the latch — a one-off is nobody's generation candidate — and it is
        // set for EVERY field, cancel and bot-driven settle included: those are exactly the per-lesson
        // facts that must survive the schedule being regenerated around them.
        if (lesson.SeriesId is not null)
            lesson.MarkCustomized();

        lessons.Update(lesson);
        await uow.SaveChangesAsync(ct);
        return new LessonPatchOutcome.Ok(lesson);
    }

    /// <summary>
    /// Settles a batch of lessons in one go — the debts screen's "mark as paid", where the tutor ticks
    /// off what a student has finally handed over. The batch is validated as a WHOLE before a single
    /// row is touched: one id nobody can resolve, or one lesson that was never taught, refuses the
    /// entire request, so the tutor is never left with half a payment recorded. Ids that are already
    /// settled are counted rather than refused — the button must survive being pressed twice, or a
    /// flaky connection retrying it.
    /// Payment flips through the very mutator a single <c>PATCH /lessons/{id}</c> uses, and a series
    /// occurrence is latched exactly as that patch latches it: a settle is a per-lesson decision the
    /// schedule generator must never regenerate away.
    /// Returns how many ids were accepted, the no-ops included; everything commits in ONE save.
    /// Whose lessons these are is never asked: the lookup is tenant-scoped, so another tutor's id is
    /// simply one nobody can resolve.
    /// </summary>
    public async Task<Result<int>> SettleAsync(
        IReadOnlyCollection<Guid> lessonIds,
        CancellationToken ct = default)
    {
        // The same id twice names one lesson and is one settle.
        var ids = lessonIds.Distinct().ToList();
        if (ids.Count == 0)
            return Result<int>.Failure(new Error(
                "Lesson.NoneSelected", "Select at least one lesson to settle.", "LessonIds"));

        var batch = await lessons.GetByIdsAsync(ids, track: true, ct);

        // Both refusals are reported together, and each once however many ids offend: the client sent
        // a selection, not a form, so it is the selection as a whole that is unhonourable.
        var errors = new List<Error>();
        if (batch.Count != ids.Count)
            errors.Add(new Error(
                "Lesson.NotFound", "Some of the selected lessons no longer exist.", "LessonIds"));
        if (batch.Any(l => l.Status != LessonStatus.Completed))
            errors.Add(new Error(
                "Lesson.NotCompleted",
                "Only lessons already recorded as completed can be settled.",
                "LessonIds"));
        if (errors.Count > 0)
            return Result<int>.Failure([.. errors]);

        foreach (var lesson in batch)
        {
            // Every row was checked as completed above, so the mutator cannot refuse it — and an
            // already-paid one takes the same call and stays exactly as it is.
            lesson.SetPaid(true);
            if (lesson.SeriesId is not null)
                lesson.MarkCustomized();
            lessons.Update(lesson);
        }

        await uow.SaveChangesAsync(ct);

        logger.LogInformation(
            "Settled {SettledCount} lesson(s) for tutor {TutorId}",
            batch.Count, batch[0].TutorTelegramId);
        return Result<int>.Success(batch.Count);
    }

    /// <summary>
    /// Whether the patch would NEWLY leave the lesson ahead of us and still to be taught — the one
    /// shape <see cref="StopTeachingAsync"/> leaves none of behind. A row already in that shape is
    /// not this patch's doing (and cannot belong to an archived student in the first place), so only
    /// the transition into it counts — which is what keeps the student lookup off the ordinary path.
    /// </summary>
    private bool ReopensSchedule(Lesson lesson, DateTimeOffset startUtc, LessonStatus status)
    {
        var now = clock.GetUtcNow();
        var wasToBeTaught = lesson.StartUtc > now && lesson.Status != LessonStatus.Completed;
        return !wasToBeTaught && startUtc > now && status != LessonStatus.Completed;
    }

    /// <summary>
    /// Creates what the one create form asked for: a one-off lesson when the request carries no
    /// <c>Repeat</c>, a weekly series when it does. Both branches read the same local <c>Date</c> +
    /// <c>StartTimeLocal</c> and resolve it through the tutor's profile time zone, so the profile is
    /// required either way, and both refuse a time that collides — or a student nobody teaches any
    /// more.
    /// </summary>
    public async Task<CreateLessonOutcome> CreateAsync(
        CreateLessonRequest request,
        CancellationToken ct = default)
    {
        // Friendlier, example-bearing message than the domain's for the most common request error.
        if (request.Repeat is { } repeat && !repeat.Weekdays.IsValidSet())
            return Invalid(new Error(
                "LessonSeries.WeekdaysRequired",
                "At least one weekday is required (e.g. \"Monday, Thursday\").",
                "Weekdays"));

        var profile = await profiles.GetAsync(ct);
        if (profile is null)
            return Invalid(new Error(
                "TutorProfile.NotSet",
                "Set your time zone first via PUT /profile — lesson times are defined in it.",
                "Profile"));

        var student = await students.GetByIdAsync(request.StudentId, ct: ct);
        // The lookup is tenant-scoped, so a student of another tutor is simply not there — same
        // message whether it is missing or someone else's, because existence must not leak.
        if (student is null)
            return Invalid(new Error("Student.NotFound", "Student not found.", "StudentId"));

        // Archiving said the tutor stopped teaching them, and the cascade behind it emptied their
        // schedule accordingly — booking a new lesson would contradict both. Both branches are refused
        // here: whether the form repeats or not, it is the same statement about the same student.
        if (student.Status == StudentStatus.Archived)
            return Invalid(new Error(
                "Student.Archived",
                "This student is archived. Restore them before booking new lessons.",
                "StudentId"));

        return request.Repeat is { } weekly
            ? await CreateSeriesAsync(request, weekly, profile.TimeZone, ct)
            : await CreateOneOffAsync(request, student.Rate, profile.TimeZone, ct);
    }

    /// <summary>
    /// The no-repeat branch: one physical lesson at the requested local wall clock, resolved to an
    /// instant through the tutor's zone by the very seam a series' occurrences go through, so the
    /// same form on the same day lands at the same moment whichever branch it takes.
    /// </summary>
    private async Task<CreateLessonOutcome> CreateOneOffAsync(
        CreateLessonRequest request,
        decimal studentRate,
        TimeZoneInfo zone,
        CancellationToken ct)
    {
        // A single lesson is placed by hand, so it must land inside the window the schedule is
        // actually planned in. The past is untouched — only the far future is refused.
        if (request.Date > PlanningHorizon.LastDateFrom(LocalToday(zone)))
            return Invalid(PlanningHorizon.Exceeded("Date"));

        var startUtc = WallClock.ToUtc(request.Date, request.StartTimeLocal, zone);

        // Validated before the end instant is computed, so an absurd duration is reported rather
        // than thrown at DateTimeOffset.
        var created = Lesson.Create(
            request.StudentId, startUtc, request.DurationMinutes,
            request.Price ?? studentRate, clock.GetUtcNow(), request.Topic);
        if (!created.IsSuccess)
            return new CreateLessonOutcome.Validation(created);
        var lesson = created.Value;

        var conflicts = await overlapChecker.CheckLessonAsync(lesson.StartUtc, lesson.EndUtc, ct: ct);
        if (conflicts.Count > 0)
            return new CreateLessonOutcome.Conflict(conflicts);

        lessons.Add(lesson);
        // The commit is also where the lesson gets its owner, so the id logged below is the stored one.
        await uow.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created one-off lesson {LessonId} for tutor {TutorId} at {StartUtc}",
            lesson.Id, lesson.TutorTelegramId, lesson.StartUtc);
        return new CreateLessonOutcome.LessonCreated(lesson);
    }

    /// <summary>
    /// Stops teaching a student, on the schedule as much as on paper: every series of theirs that can
    /// still produce lessons is ended as of today — its last possible lesson day becomes yesterday in
    /// its own time zone, exactly as cancelling one by hand does — and every lesson of theirs that has
    /// not started yet is deleted, one-offs and generated rows alike, the hand-edited ones included.
    /// A COMPLETED lesson is never touched wherever it sits: it happened and carries the payment the
    /// debt dashboard counts. Neither is a lesson already under way — the sweep selects by instant, so
    /// today's running lesson survives as the row it has always been.
    /// Everything commits in ONE save, so the schedule either forgets the student or does not.
    /// This is what makes "an archived student owns no future lesson to be taught and no running
    /// series" an invariant of the data rather than a rule every reader has to re-apply — see
    /// <see cref="LessonOverlapChecker"/>, which relies on it instead of filtering archived students
    /// itself. The sweep only establishes the invariant; three guards keep it standing while the
    /// student stays archived: booking (<see cref="CreateAsync"/>), re-opening the rule
    /// (<see cref="UpdateSeriesAsync"/>) and patching a lesson back onto their schedule —
    /// un-settling the completed row still ahead, or dragging a past one into the future
    /// (<see cref="ApplyPatchAsync"/>) — are all refused.
    /// Whose student it is is never an argument: every read below is scoped to the current tenant, so
    /// another tutor's id simply matches nothing.
    /// </summary>
    public async Task StopTeachingAsync(Guid studentId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        // The database pre-filter uses a UTC-derived cut-off one day back, a superset for every zone
        // offset; the exact call is then made per series against its own zone, the same "today" the
        // cancel route works from.
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
        var ended = 0;
        foreach (var series in await seriesRepo.GetActiveAsync(cutoff, ct))
        {
            if (series.StudentId != studentId || HasEnded(series, now))
                continue;

            series.CancelAsOf(now);
            seriesRepo.Update(series);
            ended++;
        }

        var removed = (await lessons.GetFutureForStudentAsync(studentId, now, track: true, ct))
            .Where(l => l.Status != LessonStatus.Completed)
            .ToList();
        foreach (var lesson in removed)
            lessons.Remove(lesson);

        await uow.SaveChangesAsync(ct);

        logger.LogInformation(
            "Stopped teaching student {StudentId}: {EndedCount} series ended, {RemovedCount} future lesson(s) removed",
            studentId, ended, removed.Count);
    }

    /// <summary>
    /// Whether the series can no longer produce anything — its window closed before today in its own
    /// time zone. Such a series is already stopped, so ending it again would only churn a row.
    /// </summary>
    private static bool HasEnded(LessonSeries series, DateTimeOffset now) =>
        series.EndDate is { } endDate && endDate < series.Pattern.LocalDateOf(now);

    /// <summary>
    /// Whether <paramref name="startUtc"/> falls past the horizon in the tutor's own zone. A tutor
    /// with no profile yet cannot have reached this path through the create form; UTC is the neutral
    /// fallback rather than a second failure mode.
    /// </summary>
    private async Task<bool> BeyondHorizonAsync(DateTimeOffset startUtc, CancellationToken ct)
    {
        var zone = (await profiles.GetAsync(ct))?.TimeZone ?? TimeZoneInfo.Utc;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startUtc, zone).DateTime);
        return date > PlanningHorizon.LastDateFrom(LocalToday(zone));
    }

    /// <summary>Today's calendar date in <paramref name="zone"/> — the anchor of the rolling window.</summary>
    private DateOnly LocalToday(TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);

    private static CreateLessonOutcome Invalid(Error error) =>
        new CreateLessonOutcome.Validation(Result.Failure(error));
}
