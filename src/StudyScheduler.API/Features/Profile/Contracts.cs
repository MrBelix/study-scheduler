using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>
/// Request body for creating/updating the current tutor's profile (upsert). Null fields mean
/// "leave unchanged", so partial saves don't clobber each other: <c>LanguageCode</c> null keeps
/// the language, <c>RemindMinutes</c> null keeps the reminder setting (<b>0 turns reminders
/// off</b> — null can't, or every timezone-only save would disable them), <c>NotifyAfterLesson</c>
/// null keeps the follow-up setting.
/// </summary>
public sealed record UpdateProfileRequest(
    string TimeZoneId,
    string? LanguageCode,
    int? RemindMinutes = null,
    bool? NotifyAfterLesson = null);

/// <summary>Tutor profile projection returned to the client. <c>RemindMinutes</c> null — reminders off.</summary>
public sealed record ProfileResponse(
    string TimeZoneId,
    string? LanguageCode,
    int? RemindMinutes,
    bool NotifyAfterLesson,
    DateTimeOffset CreatedAtUtc)
{
    public static ProfileResponse From(TutorProfile profile) => new(
        profile.TimeZone.Id,
        // Serialize the enum back to its lowercase wire code ("uk"/"en"); null stays null.
        profile.LanguageCode?.ToCode(),
        profile.RemindMinutes,
        profile.NotifyAfterLesson,
        profile.CreatedAtUtc);
}
