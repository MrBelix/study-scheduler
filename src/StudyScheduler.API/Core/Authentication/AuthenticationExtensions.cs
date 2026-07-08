using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace StudyScheduler.API.Core.Authentication;

public static class AuthenticationExtensions
{
    /// <summary>Registers the Telegram Mini App authentication scheme, its options and validator.</summary>
    public static IServiceCollection AddTelegramAuthentication(this IServiceCollection services)
    {
        services.AddOptions<TelegramAuthOptions>()
            .BindConfiguration("TelegramAuth")
            .Validate(o => !string.IsNullOrWhiteSpace(o.BotToken), "TelegramAuth:BotToken is required.")
            .ValidateOnStart();
        // The section must ALSO be bound to the scheme-named options instance:
        // TelegramAuthenticationHandler (an AuthenticationHandler<TelegramAuthOptions>) resolves
        // its Options via IOptionsMonitor.Get(Scheme.Name), while TelegramInitDataValidator and
        // TelegramBotClient read the unnamed IOptions<> above. Without this named binding the
        // handler would see an empty BotToken and a default MaxAuthAge. Do not merge the two.
        services.AddOptions<TelegramAuthOptions>(TelegramAuthOptions.Scheme)
            .BindConfiguration("TelegramAuth");
        services.AddSingleton<TelegramInitDataValidator>();

        services.AddAuthentication(TelegramAuthOptions.Scheme)
            .AddScheme<TelegramAuthOptions, TelegramAuthenticationHandler>(
                TelegramAuthOptions.Scheme, _ => { });
        services.AddAuthorization();

        return services;
    }

    /// <summary>Maps <c>GET /me</c> — the current authenticated Telegram user, projected from claims.</summary>
    public static IEndpointRouteBuilder MapCurrentUser(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            Id = user.FindFirstValue(ClaimTypes.NameIdentifier),
            Username = user.FindFirstValue(TelegramClaimTypes.Username),
            FirstName = user.FindFirstValue(ClaimTypes.GivenName),
            LastName = user.FindFirstValue(ClaimTypes.Surname),
        }))
        .RequireAuthorization();

        return app;
    }
}
