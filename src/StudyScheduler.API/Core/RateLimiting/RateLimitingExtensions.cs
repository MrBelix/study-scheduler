using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace StudyScheduler.API.Core.RateLimiting;

/// <summary>
/// Fixed-window rate limiting for write endpoints (POST/PATCH/PUT), partitioned per user by the
/// authenticated Telegram id (falling back to the remote IP for unauthenticated requests). The
/// limits are deliberately generous — the tenant is a single human tutor — and configurable via
/// the "RateLimiting:Write" section.
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>Policy name applied to write endpoints via <c>RequireRateLimiting</c>.</summary>
    public const string WritePolicy = "write";

    private const int DefaultPermitLimit = 60;
    private const int DefaultWindowSeconds = 60;

    public static IServiceCollection AddWriteRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimiting:Write:PermitLimit", DefaultPermitLimit);
        var window = TimeSpan.FromSeconds(
            configuration.GetValue("RateLimiting:Write:WindowSeconds", DefaultWindowSeconds));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(WritePolicy, httpContext =>
            {
                var partitionKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = window,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }
}
