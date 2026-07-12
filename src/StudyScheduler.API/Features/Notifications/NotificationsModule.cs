using Microsoft.Extensions.Options;
using StudyScheduler.API.Core.Authentication;
using Telegram.Bot;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Composition root for the Notifications feature: binds and validates options, wires the Telegram
/// transport and the runner pipeline, starts the background poller and webhook registrar, and maps
/// the webhook route. Program.cs calls <see cref="AddNotificationsFeature"/> and
/// <see cref="MapNotificationsFeature"/>.
/// </summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsFeature(this IServiceCollection services)
    {
        services.AddOptions<NotificationsOptions>()
            .BindConfiguration("Notifications")
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NotificationsOptions>, NotificationsOptionsValidator>();

        // The Telegram client is thread-safe and cheap to share, so keep one singleton, but source its
        // HttpClient from IHttpClientFactory (pooled handlers, DNS refresh) as Telegram.Bot recommends.
        services.AddHttpClient("Telegram.Bot.Client");
        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var token = sp.GetRequiredService<IOptions<TelegramAuthOptions>>().Value.BotToken;
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Telegram.Bot.Client");
            return new TelegramBotClient(token, httpClient);
        });

        services.AddScoped<INotificationSender, TelegramBotSender>();
        services.AddSingleton<NotificationPlanner>();
        services.AddSingleton<NotificationText>();
        services.AddScoped<NotificationRunner>();
        services.AddScoped<TelegramWebhookHandler>();
        services.AddHostedService<NotificationPollerService>();
        services.AddHostedService<TelegramWebhookRegistrar>();

        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsFeature(this IEndpointRouteBuilder app)
    {
        // Anonymous — Telegram calls this, not an authenticated user. The endpoint self-guards on the
        // shared secret token instead.
        app.MapPost("/telegram/webhook", Endpoints.Webhook);
        return app;
    }
}
