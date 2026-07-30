using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// Computes the tutor's accounting figures over a range. Reads through <see cref="ScheduleReader"/>
/// so virtual series occurrences are counted alongside physical lessons.
/// </summary>
public sealed class ReportSummaryCalculator(ScheduleReader reader, TimeProvider clock)
{
    public async Task<ReportSummaryResponse> CalculateAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        var entries = await reader.GetScheduleAsync(tutorTelegramId, fromUtc, toUtc, ct: ct);
        return Summarize(entries, clock.GetUtcNow());
    }

    /// <summary>The pure aggregation — the arithmetic seam the unit tests drive directly.</summary>
    internal static ReportSummaryResponse Summarize(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset nowUtc)
    {
        var billable = entries.Where(e => e.Status != LessonStatus.Cancelled).ToList();
        var planned = billable.Sum(e => e.Price);
        var actual = billable.Where(e => e.IsPaid).Sum(e => e.Price);

        return new ReportSummaryResponse(
            entries.Count,
            entries.Count(e => e.Status == LessonStatus.Completed),
            entries.Count(e => e.Status == LessonStatus.Cancelled),
            entries.Count(e => e.Status == LessonStatus.Scheduled && e.StartUtc >= nowUtc),
            // Past lessons nobody closed out — the tutor's to-do list.
            entries.Count(e => e.Status == LessonStatus.Scheduled && e.StartUtc < nowUtc),
            // No !Cancelled filter needed: the domain guarantees a cancelled lesson is never paid.
            entries.Count(e => e.IsPaid),
            planned,
            actual,
            planned - actual);
    }
}
