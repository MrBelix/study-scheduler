namespace StudyScheduler.API.Core.ErrorHandling;

/// <summary>
/// Composition root for global error handling: RFC 7807 problem-details responses for every
/// unhandled exception. Program.cs just calls <see cref="AddGlobalErrorHandling"/> and
/// <see cref="UseGlobalErrorHandling"/>.
/// </summary>
public static class ErrorHandlingExtensions
{
    public static IServiceCollection AddGlobalErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }

    public static IApplicationBuilder UseGlobalErrorHandling(this IApplicationBuilder app) =>
        app.UseExceptionHandler();
}
