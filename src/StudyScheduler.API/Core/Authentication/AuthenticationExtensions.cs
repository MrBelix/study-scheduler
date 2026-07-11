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
}
