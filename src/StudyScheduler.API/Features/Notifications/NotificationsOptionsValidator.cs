using Microsoft.Extensions.Options;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Start-time validation for <see cref="NotificationsOptions"/>. A misconfigured poll interval could
/// silently drop reminders, so these rules fail fast at boot rather than degrade at runtime.
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

        if (options.FollowUpLookbackMinutes < options.PollIntervalMinutes)
            return ValidateOptionsResult.Fail(
                $"Notifications:FollowUpLookbackMinutes ({options.FollowUpLookbackMinutes}) must be at " +
                $"least PollIntervalMinutes ({options.PollIntervalMinutes}) so a just-ended lesson is " +
                $"still eligible on the next tick.");

        // A webhook URL with no secret can't be secured: the endpoint would have nothing to match the
        // X-Telegram-Bot-Api-Secret-Token header against. Both empty = webhook disabled = valid.
        if (!string.IsNullOrEmpty(options.WebhookUrl) && string.IsNullOrEmpty(options.WebhookSecret))
            return ValidateOptionsResult.Fail(
                "Notifications:WebhookSecret is required when Notifications:WebhookUrl is set " +
                "(the webhook endpoint can't be secured without it).");

        return ValidateOptionsResult.Success;
    }
}
