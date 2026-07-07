using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace StudyScheduler.API.Core.ErrorHandling;

/// <summary>
/// Funnels every unhandled exception into an RFC 7807 response. Domain guard failures
/// (<see cref="ArgumentException"/>, including <see cref="ArgumentOutOfRangeException"/>) become
/// 400s with a validation-style payload keyed by the offending parameter; everything else becomes
/// an opaque 500 — exception details are only included in Development.
/// </summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails problemDetails;
        if (exception is ArgumentException argumentException)
        {
            logger.LogWarning(
                argumentException,
                "Domain guard rejected {Method} {Path}",
                httpContext.Request.Method,
                httpContext.Request.Path);

            problemDetails = new HttpValidationProblemDetails(new Dictionary<string, string[]>
            {
                [argumentException.ParamName ?? "request"] = [argumentException.Message],
            })
            {
                Status = StatusCodes.Status400BadRequest,
            };
        }
        else
        {
            logger.LogError(
                exception,
                "Unhandled exception processing {Method} {Path}",
                httpContext.Request.Method,
                httpContext.Request.Path);

            problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
            };
            if (environment.IsDevelopment())
                problemDetails.Detail = exception.ToString();
        }

        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }
}
