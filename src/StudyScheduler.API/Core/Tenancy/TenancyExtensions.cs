using StudyScheduler.API.Core.Authentication;

namespace StudyScheduler.API.Core.Tenancy;

public static class TenancyExtensions
{
    /// <summary>Registers the scoped tenant holder behind its read and write seams.</summary>
    public static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        // One instance per scope behind both interfaces — the reader (AppDbContext) and the writer
        // (this middleware, the webhook, the background passes) must see the very same value.
        services.AddScoped<TutorContext>();
        services.AddScoped<ITutorContext>(sp => sp.GetRequiredService<TutorContext>());
        services.AddScoped<ITutorScope>(sp => sp.GetRequiredService<TutorContext>());
        return services;
    }

    /// <summary>
    /// Establishes the tenant of every authenticated request from the identity the Telegram scheme
    /// already validated — no init data is parsed or re-checked here. Must run after
    /// <c>UseAuthentication</c> and before anything that touches the database. Anonymous requests
    /// leave the scope tenant-less, which reads nothing.
    /// </summary>
    public static IApplicationBuilder UseTenancy(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
                context.RequestServices.GetRequiredService<ITutorScope>()
                    .SetFromAuthentication(context.User.GetTelegramId());

            await next(context);
        });
}
