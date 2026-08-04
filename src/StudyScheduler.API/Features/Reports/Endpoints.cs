using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.ErrorHandling;

namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// HTTP handlers for the Reports feature. Wired to routes in <see cref="ReportsModule"/>.
/// "The current tutor" is the scope's tenant: every figure below is read through it.
/// </summary>
internal static class Endpoints
{
    private const string AnchorFormat = "yyyy-MM-dd";

    /// <summary>
    /// Returns everything the Money screen shows for one reporting window: income (received,
    /// expected, and received over the same window one period earlier), the all-time debt ledger,
    /// lesson counts, weekly load, chart buckets and per-student income.
    /// <c>anchor</c> is any date inside the wanted window — omitted, it is today in the tutor's own
    /// time zone, which is also where the window boundaries are resolved. Reads never write.
    /// </summary>
    public static async Task<Results<Ok<DashboardResponse>, ValidationProblem>> GetDashboard(
        string? period,
        string? anchor,
        ReportDashboardService service,
        CancellationToken ct)
    {
        // Both parameters stay strings so an unknown value yields a clean ValidationProblem rather
        // than a JSON/route-binding 400 with no field to attach to.
        if (DashboardPeriod.ParseKind(period) is not { } kind)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Period"] = ["'period' must be one of: week, month, quarter."],
            });

        DateOnly? anchorDate = null;
        if (!string.IsNullOrWhiteSpace(anchor))
        {
            if (!DateOnly.TryParseExact(
                    anchor, AnchorFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Anchor"] = [$"'anchor' must be a date in {AnchorFormat} format."],
                });

            anchorDate = parsed;
        }

        var dashboard = await service.GetAsync(kind, anchorDate, ct);
        return dashboard.IsSuccess
            ? TypedResults.Ok(dashboard.Value)
            : dashboard.ToValidationProblem();
    }
}
