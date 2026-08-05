using Microsoft.Extensions.Options;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Start-time validation for <see cref="NotificationsOptions"/>. A misconfigured poll interval could
/// silently drop reminders and a malformed Mini App URL would produce dead buttons, so these rules
/// fail fast at boot rather than degrade at runtime.
/// </summary>
public sealed class NotificationsOptionsValidator : IValidateOptions<NotificationsOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationsOptions options)
    {
        if (options.PollIntervalMinutes < 1)
            return ValidateOptionsResult.Fail(
                $"Notifications:PollIntervalMinutes must be at least 1 (was {options.PollIntervalMinutes}).");

        // Ticking slower than the shortest possible reminder lead time would let a reminder fall
        // entirely between two ticks and never fire.
        if (options.PollIntervalMinutes > TutorProfile.MinRemindMinutes)
            return ValidateOptionsResult.Fail(
                $"Notifications:PollIntervalMinutes must be at most {TutorProfile.MinRemindMinutes} " +
                $"(the minimum reminder lead time) so no reminder is skipped between ticks " +
                $"(was {options.PollIntervalMinutes}).");

        // Zero grace is legal (send the summary the moment the day's last lesson ends); negative
        // would mean sending it before.
        if (options.SummaryGraceMinutes < 0)
            return ValidateOptionsResult.Fail(
                $"Notifications:SummaryGraceMinutes must be zero or positive (was {options.SummaryGraceMinutes}).");

        // A tick that may make no calls at all would never drain anything.
        if (options.MaxTelegramCallsPerTick < 1)
            return ValidateOptionsResult.Fail(
                $"Notifications:MaxTelegramCallsPerTick must be at least 1 (was {options.MaxTelegramCallsPerTick}).");

        // Empty is valid and means "emit no url buttons" (dev/test). Anything else has to be a URL we
        // can append "?startapp=" to: absolute, https, and carrying no query string of its own.
        if (!string.IsNullOrEmpty(options.MiniAppUrl))
        {
            if (!Uri.TryCreate(options.MiniAppUrl, UriKind.Absolute, out var miniApp)
                || miniApp.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(miniApp.Query))
                return ValidateOptionsResult.Fail(
                    "Notifications:MiniAppUrl must be an absolute https URL with no query string " +
                    $"(\"?startapp=\" is appended to it), or empty to emit no deep-link buttons (was '{options.MiniAppUrl}').");
        }

        // A webhook URL with no secret can't be secured: the endpoint would have nothing to match the
        // X-Telegram-Bot-Api-Secret-Token header against. Both empty = webhook disabled = valid.
        if (!string.IsNullOrEmpty(options.WebhookUrl) && string.IsNullOrEmpty(options.WebhookSecret))
            return ValidateOptionsResult.Fail(
                "Notifications:WebhookSecret is required when Notifications:WebhookUrl is set " +
                "(the webhook endpoint can't be secured without it).");

        return ValidateOptionsResult.Success;
    }
}
