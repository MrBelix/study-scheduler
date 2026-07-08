using Microsoft.EntityFrameworkCore;
using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.API.Core.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers <see cref="AppDbContext"/> for the "Default" connection via the Aspire client
    /// integration (health checks, connection retries, telemetry), and the unit of work that
    /// commits what repositories stage into it.
    /// </summary>
    public static void AddPersistence(this IHostApplicationBuilder builder)
    {
        builder.AddSqlServerDbContext<AppDbContext>("Default");
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
