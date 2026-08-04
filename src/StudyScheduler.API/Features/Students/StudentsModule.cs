using StudyScheduler.API.Core.RateLimiting;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>
/// Composition root for the Students feature: registers its services and maps its routes.
/// Program.cs just calls <see cref="AddStudentsFeature"/> and <see cref="MapStudentsFeature"/>.
/// </summary>
public static class StudentsModule
{
    public static IServiceCollection AddStudentsFeature(this IServiceCollection services)
    {
        services.AddScoped<IStudentRepository, EfStudentRepository>();
        return services;
    }

    public static IEndpointRouteBuilder MapStudentsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/students").RequireAuthorization();

        group.MapGet("/", Endpoints.GetMine);
        // Literal segment; the id route below is guid-constrained, so the two never compete.
        group.MapGet("/archived", Endpoints.GetArchived);
        group.MapGet("/{id:guid}", Endpoints.GetById);
        // What the details screen's debt banner opens: the lessons the banner sums up.
        group.MapGet("/{id:guid}/debts", Endpoints.GetDebts);
        group.MapPost("/", Endpoints.Create)
            .RequireRateLimiting(RateLimitingExtensions.WritePolicy);
        group.MapPatch("/{id:guid}", Endpoints.Update)
            .RequireRateLimiting(RateLimitingExtensions.WritePolicy);

        return app;
    }
}
