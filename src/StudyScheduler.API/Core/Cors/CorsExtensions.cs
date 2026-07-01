namespace StudyScheduler.API.Core.Cors;

public static class CorsExtensions
{
    private const string PolicyName = "MiniApp";

    /// <summary>
    /// CORS for the Mini App client. Auth travels in the Authorization header (no cookies), so
    /// credentials aren't needed. In Development any origin is allowed to ease localhost/ngrok
    /// testing; production uses the configured allow-list.
    /// </summary>
    public static IServiceCollection AddMiniAppCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            if (environment.IsDevelopment() && origins.Length == 0)
                policy.SetIsOriginAllowed(_ => true);
            else
                policy.WithOrigins(origins);
            policy.AllowAnyHeader().AllowAnyMethod();
        }));

        return services;
    }

    public static WebApplication UseMiniAppCors(this WebApplication app)
    {
        app.UseCors(PolicyName);
        return app;
    }
}
