using StudyScheduler.API.Core.Persistence;
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
        group.MapGet("/{id:guid}", Endpoints.GetById);
        group.MapPost("/", Endpoints.Create);
        group.MapPatch("/{id:guid}", Endpoints.Update);

        return app;
    }
}
