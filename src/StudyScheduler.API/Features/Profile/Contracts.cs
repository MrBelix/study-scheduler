using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>
/// Request body for creating/updating the current tutor's profile (upsert).
/// <c>LanguageCode</c> null means "leave unchanged" — the language is never cleared.
/// </summary>
public sealed record UpdateProfileRequest(string TimeZoneId, string? LanguageCode);

/// <summary>Tutor profile projection returned to the client.</summary>
public sealed record ProfileResponse(
    string TimeZoneId,
    string? LanguageCode,
    DateTimeOffset CreatedAtUtc)
{
    public static ProfileResponse From(TutorProfile profile) => new(
        profile.TimeZone.Id,
        profile.LanguageCode,
        profile.CreatedAtUtc);
}
