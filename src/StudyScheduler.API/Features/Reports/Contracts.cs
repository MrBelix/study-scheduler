namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// The tutor's accounting figures over a requested range. Counts cover every schedule entry
/// (physical lessons and virtual series occurrences alike); the money figures exclude cancelled
/// entries, which are owed nothing.
/// </summary>
public sealed record ReportSummaryResponse(
    int TotalCount,
    int CompletedCount,
    int CancelledCount,
    int UpcomingCount,
    int UnclosedCount,
    int PaidCount,
    decimal PlannedIncome,
    decimal ActualIncome,
    decimal OutstandingIncome);
