namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// The tutor's accounting figures over a requested range. Counts cover every lesson in the window,
/// one-off and series-generated alike; the money figures exclude cancelled lessons, which are owed
/// nothing.
/// </summary>
public sealed record ReportSummaryResponse(
    int CompletedCount,
    int CancelledCount,
    int UpcomingCount,
    int UnclosedCount,
    decimal PlannedIncome,
    decimal ActualIncome);

/// <summary>The reporting window, as inclusive local dates in the tutor's own time zone.</summary>
public sealed record DashboardPeriodResponse(DateOnly From, DateOnly To);

/// <summary>
/// The period's money. <c>Actual</c> is what was received (paid, non-cancelled); <c>Expected</c> is
/// what the period is worth once everything non-cancelled in it is settled. Both are
/// <see cref="ReportSummaryResponse.ActualIncome"/> and <see cref="ReportSummaryResponse.PlannedIncome"/>
/// verbatim, so the screen cannot drift from the shared calculator. <c>Previous</c> is
/// <c>Actual</c> over the same window one period earlier.
/// </summary>
public sealed record DashboardIncomeResponse(decimal Actual, decimal Expected, decimal Previous);

/// <summary>One student who owes money, with the name resolved server-side.</summary>
public sealed record DebtorResponse(
    Guid StudentId,
    string Name,
    decimal Amount,
    int LessonsCount,
    DateTimeOffset OldestUtc);

/// <summary>
/// The debt ledger — unpaid completed lessons over the tutor's whole history, not just the period,
/// ordered by amount owed. A debt does not expire because the reporting window moved on.
/// </summary>
public sealed record DashboardDebtResponse(decimal Total, IReadOnlyList<DebtorResponse> Debtors);

/// <summary>Schedule entries in the period by status; every entry falls in exactly one bucket.</summary>
public sealed record DashboardLessonsResponse(int Completed, int Scheduled, int Cancelled);

/// <summary>
/// How busy the period is: <c>Hours</c> is the average non-cancelled teaching hours per week over
/// it (rounded to one decimal), <c>LessonsInPeriod</c> the non-cancelled entries behind that average.
/// </summary>
public sealed record WeeklyLoadResponse(decimal Hours, int LessonsInPeriod);

/// <summary>
/// One chart column: a day for a week period, a Monday-based calendar week otherwise — clipped to
/// the period, so the first and last bucket of a month or quarter are usually partial weeks.
/// </summary>
public sealed record DashboardBucketResponse(DateOnly From, DateOnly To, int CompletedCount, int ScheduledCount);

/// <summary>One student's received income in the period, with the name resolved server-side.</summary>
public sealed record StudentIncomeResponse(Guid StudentId, string Name, decimal Income);

/// <summary>
/// Everything the redesigned Money screen shows for one reporting window, in a single response —
/// the client renders it without deriving anything.
/// </summary>
public sealed record DashboardResponse(
    DashboardPeriodResponse Period,
    DashboardIncomeResponse Income,
    DashboardDebtResponse Debt,
    DashboardLessonsResponse Lessons,
    WeeklyLoadResponse WeeklyLoad,
    IReadOnlyList<DashboardBucketResponse> Buckets,
    IReadOnlyList<StudentIncomeResponse> PerStudent);
