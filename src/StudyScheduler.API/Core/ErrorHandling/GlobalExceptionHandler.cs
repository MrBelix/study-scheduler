using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace StudyScheduler.API.Core.ErrorHandling;

/// <summary>
/// Funnels every unhandled exception into an opaque RFC 7807 response — details are only included
/// in Development. User-input validation reaches the client as ValidationProblem via domain
/// <c>Result</c>s, never as exceptions, so anything landing here is a genuine bug or data anomaly
/// and answers 500; translating app exceptions into 400s would leak internals to the caller.
/// The one exception is <see cref="BadHttpRequestException"/>, which is the framework's own
/// "this request is malformed" signal and already carries the status it would have answered with.
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
        // Kestrel (oversized body, bad encoding) and minimal-API parameter binding raise
        // BadHttpRequestException with the status the client deserves. Binding failures only reach
        // us when RouteHandlerOptions.ThrowOnBadRequest is on — the default in Development — so
        // without this a malformed route value would answer 400 in production and 500 locally.
        var statusCode = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;

        // A malformed request is the caller's mistake, not a server fault: log it, but don't page
        // anyone over it, and don't let a client flood the error stream.
        logger.Log(
            statusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Warning,
            exception,
            "Unhandled exception processing {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            // Anything else gets its canonical title ("Bad Request", ...) from the problem details
            // service; only the deliberately opaque 500 needs wording of its own.
            Title = statusCode == StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred."
                : null,
        };
        if (environment.IsDevelopment())
            problemDetails.Detail = exception.ToString();

        httpContext.Response.StatusCode = statusCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }
}
