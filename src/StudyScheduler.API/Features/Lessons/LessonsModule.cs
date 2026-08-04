using StudyScheduler.API.Core.RateLimiting;
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
        // The one façade the endpoints, the bot's webhook and the Students feature talk to; lessons
        // and series alike, reads as much as writes. Everything else here is its machinery.
        services.AddScoped<LessonService>();
        services.AddScoped<LessonGenerator>();
        services.AddHostedService<LessonGenerationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapLessonsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lessons").RequireAuthorization();

        group.MapGet("/", Endpoints.GetMine);
        // One create route for both branches of the form: with "repeat" it is a series, without it a
        // single lesson. The client never picks a route.
        group.MapPost("/", Endpoints.Create)
            .RequireRateLimiting(RateLimitingExtensions.WritePolicy);
        // Paying for lessons is one statement about many of them, so it is one request: a batch of
        // ids rather than a PATCH per lesson.
        group.MapPost("/settle", Endpoints.Settle)
            .RequireRateLimiting(RateLimitingExtensions.WritePolicy);

        // The literal "series" segment outranks the {id} parameter in routing, so the series routes
        // below are never shadowed — and the :guid constraint would keep them apart anyway.
        group.MapGet("/series", Endpoints.GetSeriesList);
        group.MapGet("/series/{seriesId:guid}", Endpoints.GetSeriesById);
        group.MapPatch("/series/{seriesId:guid}", Endpoints.UpdateSeries)
            .RequireRateLimiting(RateLimitingExtensions.WritePolicy);
        group.MapPost("/series/{seriesId:guid}/cancel", Endpoints.CancelSeries)
            .RequireRateLimiting(RateLimitingExtensions.WritePolicy);

        // One addressing scheme for every lesson: the id of its row. Anything that is not a GUID
        // matches no route at all, so it is answered with 404 before a handler is chosen.
        group.MapGet("/{id:guid}", Endpoints.GetById);
        group.MapPatch("/{id:guid}", Endpoints.Update)
            .RequireRateLimiting(RateLimitingExtensions.WritePolicy);

        return app;
    }
}
