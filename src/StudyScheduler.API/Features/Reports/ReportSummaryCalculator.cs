using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// Computes the tutor's accounting figures over a range, straight off the lesson rows — series
/// lessons included, since a series writes them out across the planning horizon. The rows are read
/// by the caller: this is pure arithmetic over the schedule it is handed.
/// </summary>
public static class ReportSummaryCalculator
{
    /// <summary>The pure aggregation — the arithmetic seam the unit tests drive directly.</summary>
    internal static ReportSummaryResponse Summarize(IReadOnlyList<Lesson> schedule, DateTimeOffset nowUtc)
    {
        var billable = schedule.Where(l => l.Status != LessonStatus.Cancelled).ToList();
        var planned = billable.Sum(l => l.Price);
        var actual = billable.Where(l => l.IsPaid).Sum(l => l.Price);

        return new ReportSummaryResponse(
            schedule.Count(l => l.Status == LessonStatus.Completed),
            schedule.Count(l => l.Status == LessonStatus.Cancelled),
            schedule.Count(l => l.Status == LessonStatus.Scheduled && l.StartUtc >= nowUtc),
            // Past lessons nobody closed out — the tutor's to-do list.
            schedule.Count(l => l.Status == LessonStatus.Scheduled && l.StartUtc < nowUtc),
            planned,
            actual);
    }
}
