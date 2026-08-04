using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// Assembles the Money screen's whole payload for one reporting window: the lessons of the period and
/// of the one before it (plain range queries — a series has already written its lessons out as rows),
/// the all-time debt ledger and the student names, in a fixed number of bulk queries — never one per
/// student. Every query reads the scope's tenant, so the payload is one tutor's by construction; the
/// id is read here for the log lines and nothing else. Nothing is written.
/// </summary>
public sealed class ReportDashboardService(
    ITutorProfileRepository profiles,
    ILessonRepository lessons,
    IStudentDebtReader debts,
    IStudentRepository students,
    ITutorContext tutor,
    TimeProvider clock,
    ILogger<ReportDashboardService> logger)
{
    /// <summary>
    /// The dashboard for the window of <paramref name="kind"/> containing <paramref name="anchor"/>
    /// — or containing today in the tutor's own time zone when no anchor is given. Fails only when
    /// the tutor has no profile yet: the window boundaries are local dates, and there is no honest
    /// zone to resolve them in until one is saved.
    /// </summary>
    public async Task<Result<DashboardResponse>> GetAsync(
        DashboardPeriodKind kind,
        DateOnly? anchor,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetAsync(ct);
        if (profile is null)
        {
            logger.LogInformation(
                "Dashboard refused for tutor {TutorId}: no profile yet", tutor.CurrentTutorTelegramId);
            return Result<DashboardResponse>.Failure(new Error(
                "TutorProfile.NotSet",
                "Set your time zone first via PUT /profile — report periods are resolved in it.",
                "Profile"));
        }

        var zone = profile.TimeZone;
        var nowUtc = clock.GetUtcNow();
        // "Which period am I in?" is a question about the tutor's calendar, not UTC's: near local
        // midnight the two are on different days, and at a month boundary on different months.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var period = DashboardPeriod.Resolve(kind, anchor ?? today);

        var (fromUtc, toUtc) = period.ToUtcWindow(zone);
        var (previousFromUtc, previousToUtc) = period.Previous.ToUtcWindow(zone);

        var schedule = await lessons.GetInRangeAsync(fromUtc, toUtc, ct: ct);
        var previousSchedule = await lessons.GetInRangeAsync(previousFromUtc, previousToUtc, ct: ct);
        var ledger = await debts.GetAllTimeAsync(ct);
        var names = await ResolveNamesAsync(schedule, ledger, ct);

        logger.LogInformation(
            "Dashboard for tutor {TutorId}: {PeriodKind} {From}..{To}, {LessonCount} lessons, {DebtorCount} debtors",
            tutor.CurrentTutorTelegramId, kind, period.From, period.To, schedule.Count, ledger.Count);

        return Result<DashboardResponse>.Success(
            Build(period, schedule, previousSchedule, ledger, names, zone, nowUtc));
    }

    /// <summary>
    /// The pure aggregation — the arithmetic seam the unit tests drive directly, with no database
    /// behind it. Every money figure comes out of <see cref="ReportSummaryCalculator.Summarize"/>
    /// rather than being recomputed here, so one set of accounting rules serves the whole screen.
    /// </summary>
    internal static DashboardResponse Build(
        DashboardPeriod period,
        IReadOnlyList<Lesson> schedule,
        IReadOnlyList<Lesson> previousSchedule,
        IReadOnlyList<StudentDebt> debts,
        IReadOnlyDictionary<Guid, string> names,
        TimeZoneInfo zone,
        DateTimeOffset nowUtc)
    {
        var summary = ReportSummaryCalculator.Summarize(schedule, nowUtc);
        var previous = ReportSummaryCalculator.Summarize(previousSchedule, nowUtc);
        var billable = schedule.Where(l => l.Status != LessonStatus.Cancelled).ToList();

        return new DashboardResponse(
            new DashboardPeriodResponse(period.From, period.To),
            new DashboardIncomeResponse(summary.ActualIncome, summary.PlannedIncome, previous.ActualIncome),
            BuildDebt(debts, names),
            new DashboardLessonsResponse(
                summary.CompletedCount,
                // The summary splits Scheduled by whether it is still ahead; the dashboard does not,
                // so the two halves go back together instead of a second status filter being written.
                summary.UpcomingCount + summary.UnclosedCount,
                summary.CancelledCount),
            new WeeklyLoadResponse(WeeklyHours(billable, period), billable.Count),
            BuildBuckets(period, schedule, zone),
            BuildPerStudent(schedule, names, nowUtc));
    }

    /// <summary>Names for every student the payload mentions — lessons and debtors — in one query.</summary>
    private async Task<Dictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Lesson> schedule,
        IReadOnlyList<StudentDebt> debts,
        CancellationToken ct)
    {
        var ids = schedule.Select(l => l.StudentId)
            .Concat(debts.Select(d => d.StudentId))
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        return (await students.GetByIdsAsync(ids, ct))
            .ToDictionary(s => s.Id, s => s.Name);
    }

    /// <summary>Average non-cancelled teaching hours per week, over the period's own length.</summary>
    private static decimal WeeklyHours(IReadOnlyList<Lesson> billable, DashboardPeriod period) =>
        Math.Round(
            billable.Sum(l => l.DurationMinutes) / 60m / (period.DayCount / 7m),
            1,
            MidpointRounding.AwayFromZero);

    private static DashboardDebtResponse BuildDebt(
        IReadOnlyList<StudentDebt> debts,
        IReadOnlyDictionary<Guid, string> names) =>
        new(
            debts.Sum(d => d.Amount),
            debts
                .Select(d => new DebtorResponse(
                    d.StudentId, Name(names, d.StudentId), d.Amount, d.LessonsCount, d.OldestUtc))
                .OrderByDescending(d => d.Amount)
                // Equal amounts would otherwise come back in whatever order the ledger produced them,
                // which makes the list jitter between refreshes.
                .ThenBy(d => d.Name, StringComparer.Ordinal)
                .ToList());

    private static List<DashboardBucketResponse> BuildBuckets(
        DashboardPeriod period,
        IReadOnlyList<Lesson> schedule,
        TimeZoneInfo zone)
    {
        var buckets = period.SplitIntoBuckets();
        var completed = new int[buckets.Count];
        var scheduled = new int[buckets.Count];

        foreach (var lesson in schedule)
        {
            var index = BucketIndex(buckets, LocalDateOf(lesson.StartUtc, zone));
            if (lesson.Status == LessonStatus.Completed)
                completed[index]++;
            else if (lesson.Status == LessonStatus.Scheduled)
                scheduled[index]++;
        }

        return buckets
            .Select((b, i) => new DashboardBucketResponse(b.From, b.To, completed[i], scheduled[i]))
            .ToList();
    }

    private static List<StudentIncomeResponse> BuildPerStudent(
        IReadOnlyList<Lesson> schedule,
        IReadOnlyDictionary<Guid, string> names,
        DateTimeOffset nowUtc) =>
        schedule
            .GroupBy(l => l.StudentId)
            // The very same summary the period total comes from, applied to one student's slice —
            // so the parts provably add up to income.actual instead of following a parallel formula.
            .Select(g => new StudentIncomeResponse(
                g.Key,
                Name(names, g.Key),
                ReportSummaryCalculator.Summarize([.. g], nowUtc).ActualIncome))
            .Where(s => s.Income != 0m)
            .OrderByDescending(s => s.Income)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The bucket a lesson belongs to, by its local start date. Buckets are contiguous and cover the
    /// whole period, so the first bucket whose end is not before the date owns it. A lesson that
    /// straddles a period boundary is pulled into the window by its end and can therefore start a few
    /// hours outside it; clamping it into the nearest bucket keeps the columns adding up to the
    /// period's own lesson counts.
    /// </summary>
    private static int BucketIndex(IReadOnlyList<(DateOnly From, DateOnly To)> buckets, DateOnly localDate)
    {
        for (var i = 0; i < buckets.Count; i++)
        {
            if (localDate <= buckets[i].To)
                return i;
        }

        return buckets.Count - 1;
    }

    private static DateOnly LocalDateOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// Data anomaly guard: a student missing from the bulk lookup must not take the whole dashboard
    /// down, and their money must not silently vanish.
    /// </summary>
    private static string Name(IReadOnlyDictionary<Guid, string> names, Guid studentId) =>
        names.GetValueOrDefault(studentId, string.Empty);
}
