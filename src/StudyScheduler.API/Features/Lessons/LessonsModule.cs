using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Composition root for the Lessons feature: registers its services and maps its routes.
/// Program.cs just calls <see cref="AddLessonsFeature"/> and <see cref="MapLessonsFeature"/>.
/// </summary>
public static class LessonsModule
{
    public static IServiceCollection AddLessonsFeature(this IServiceCollection services)
    {
        services.AddScoped<ILessonRepository, EfLessonRepository>();
        services.AddScoped<ILessonSeriesRepository, EfLessonSeriesRepository>();
        services.AddScoped<LessonOverlapChecker>();
        services.AddScoped<LessonMaterializer>();
        return services;
    }

    public static IEndpointRouteBuilder MapLessonsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lessons").RequireAuthorization();

        group.MapGet("/", Endpoints.GetMine);
        group.MapGet("/{id:guid}", Endpoints.GetById);
        group.MapPost("/", Endpoints.Create);
        group.MapPatch("/{id:guid}", Endpoints.Update);

        // The {id:guid} constraint keeps the literal "series" segment from binding to GetById.
        group.MapGet("/series", Endpoints.GetSeriesList);
        group.MapGet("/series/{seriesId:guid}", Endpoints.GetSeriesById);
        group.MapPost("/series", Endpoints.CreateSeries);
        group.MapPatch("/series/{seriesId:guid}", Endpoints.UpdateSeries);
        group.MapPost("/series/{seriesId:guid}/cancel", Endpoints.CancelSeries);

        return app;
    }
}
