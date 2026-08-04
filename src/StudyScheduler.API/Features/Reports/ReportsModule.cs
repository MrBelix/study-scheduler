namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// Composition root for the Reports feature: registers its services and maps its routes.
/// Program.cs just calls <see cref="AddReportsFeature"/> and <see cref="MapReportsFeature"/>.
/// The lesson repository it reads through is registered by the Lessons feature — its primary
/// consumer — and resolved from the same container.
/// </summary>
public static class ReportsModule
{
    public static IServiceCollection AddReportsFeature(this IServiceCollection services)
    {
        services.AddScoped<ReportDashboardService>();
        services.AddScoped<IStudentDebtReader, EfStudentDebtReader>();
        return services;
    }

    public static IEndpointRouteBuilder MapReportsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reports").RequireAuthorization();

        group.MapGet("/dashboard", Endpoints.GetDashboard);

        return app;
    }
}
