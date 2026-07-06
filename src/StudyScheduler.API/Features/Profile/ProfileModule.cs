using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>
/// Composition root for the Profile feature: registers its services and maps its routes.
/// Program.cs just calls <see cref="AddProfileFeature"/> and <see cref="MapProfileFeature"/>.
/// </summary>
public static class ProfileModule
{
    public static IServiceCollection AddProfileFeature(this IServiceCollection services)
    {
        services.AddScoped<ITutorProfileRepository, EfTutorProfileRepository>();
        return services;
    }

    public static IEndpointRouteBuilder MapProfileFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/profile").RequireAuthorization();

        group.MapGet("/", Endpoints.Get);
        group.MapPut("/", Endpoints.Put);

        return app;
    }
}
