using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.API.Core.ErrorHandling;

/// <summary>
/// The single bridge from failed domain <see cref="Result"/>s to the API's ValidationProblem
/// shape: errors grouped by <see cref="Error.Field"/>, so payload keys stay byte-identical to
/// the old endpoint-side validators ("DurationMinutes", "Price", "Topic", ...).
/// </summary>
internal static class ResultExtensions
{
    public static ValidationProblem ToValidationProblem(this Result result) =>
        TypedResults.ValidationProblem(result.Errors
            // A field-less error has no form control to attach to; "General" mirrors the
            // catch-all key style already used by e.g. the "Profile" series precondition.
            .GroupBy(e => e.Field ?? "General")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()));
}
