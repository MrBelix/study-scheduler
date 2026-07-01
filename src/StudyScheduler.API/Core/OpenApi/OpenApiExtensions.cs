using Scalar.AspNetCore;
using StudyScheduler.API.Core.Authentication;

namespace StudyScheduler.API.Core.OpenApi;

public static class OpenApiExtensions
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
            options.AddDocumentTransformer<TelegramSecuritySchemeTransformer>());
        return services;
    }

    /// <summary>Exposes the OpenAPI document and Scalar UI in Development only.</summary>
    public static WebApplication UseApiDocumentation(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }
}
