using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Composition root for the Notifications feature: the bot client, the reminder/follow-up
/// poller and the Telegram webhook. Program.cs just calls <see cref="AddNotificationsFeature"/>
/// and <see cref="MapNotificationsFeature"/>.
/// </summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsFeature(this IServiceCollection services)
    {
        services.AddOptions<NotificationsOptions>().BindConfiguration(NotificationsOptions.SectionName);
        services.AddScoped<ILessonNotificationRepository, EfLessonNotificationRepository>();
        services.AddScoped<LessonNotifier>();
        services.AddScoped<TelegramWebhookHandler>();
        services.AddHttpClient<ITelegramBotClient, TelegramBotClient>(client =>
            client.BaseAddress = new Uri("https://api.telegram.org/"));
        services.AddHostedService<LessonNotificationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsFeature(this IEndpointRouteBuilder app)
    {
        app.MapPost("/telegram/webhook", Endpoints.Webhook).AllowAnonymous();
        return app;
    }
}
