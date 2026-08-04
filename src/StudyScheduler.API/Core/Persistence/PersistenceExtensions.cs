using Microsoft.EntityFrameworkCore;
using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.API.Core.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers <see cref="AppDbContext"/> for the "Default" connection and the unit of work that
    /// commits what repositories stage into it.
    /// The context is registered by hand and then enriched, rather than through Aspire's
    /// <c>AddNpgsqlDbContext</c>: that helper POOLS the context, and a pooled DbContext may not take
    /// constructor dependencies — <see cref="AppDbContext"/> needs the scoped tenant to filter by.
    /// <c>EnrichNpgsqlDbContext</c> adds back everything the integration would have configured
    /// (health check, connection retries, telemetry).
    /// </summary>
    public static void AddPersistence(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        builder.EnrichNpgsqlDbContext<AppDbContext>();
        builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
    }

    /// <summary>
    /// Applies pending migrations at startup. Fine for a single-instance deployment; switch to an
    /// explicit <c>dotnet ef database update</c> step if you ever scale out.
    /// </summary>
    public static WebApplication ApplyMigrations(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
        return app;
    }
}
