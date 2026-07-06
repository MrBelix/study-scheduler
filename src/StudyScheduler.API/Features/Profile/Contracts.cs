using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>Request body for creating/updating the current tutor's profile (upsert).</summary>
public sealed record UpdateProfileRequest(string TimeZoneId);

/// <summary>Tutor profile projection returned to the client.</summary>
public sealed record ProfileResponse(
    string TimeZoneId,
    DateTimeOffset CreatedAtUtc)
{
    public static ProfileResponse From(TutorProfile profile) => new(
        profile.TimeZone.Id,
        profile.CreatedAtUtc);
}
