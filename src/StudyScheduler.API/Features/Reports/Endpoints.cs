using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;

namespace StudyScheduler.API.Features.Reports;

/// <summary>HTTP handlers for the Reports feature. Wired to routes in <see cref="ReportsModule"/>.</summary>
internal static class Endpoints
{
    private const int MaxRangeDays = 366;

    /// <summary>
    /// Returns the current tutor's accounting summary over <c>[from, to)</c> — counts per status
    /// plus planned / actual / outstanding income. Reads never write.
    /// </summary>
    public static async Task<Results<Ok<ReportSummaryResponse>, ValidationProblem>> GetSummary(
        DateTimeOffset from,
        DateTimeOffset to,
        ClaimsPrincipal principal,
        ReportSummaryCalculator calculator,
        CancellationToken ct)
    {
        if (ValidateRange(from, to) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var summary = await calculator.CalculateAsync(principal.GetTelegramId(), from, to, ct);
        return TypedResults.Ok(summary);
    }

    private static Dictionary<string, string[]>? ValidateRange(DateTimeOffset from, DateTimeOffset to)
    {
        var errors = new Dictionary<string, string[]>();
        if (to <= from)
            errors["To"] = ["'to' must be after 'from'."];
        else if ((to - from).TotalDays > MaxRangeDays)
            errors["To"] = [$"Range must not exceed {MaxRangeDays} days."];

        return errors.Count == 0 ? null : errors;
    }
}
