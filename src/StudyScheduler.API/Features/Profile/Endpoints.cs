using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>HTTP handlers for the Profile feature. Wired to routes in <see cref="ProfileModule"/>.</summary>
internal static class Endpoints
{
    /// <summary>Returns the current tutor's profile, 404 until it is first saved.</summary>
    public static async Task<Results<Ok<ProfileResponse>, NotFound>> Get(
        ClaimsPrincipal principal,
        ITutorProfileRepository repo)
    {
        var profile = await repo.GetAsync(principal.GetTelegramId());
        return profile is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ProfileResponse.From(profile));
    }

    /// <summary>Creates or updates the current tutor's profile (upsert).</summary>
    public static async Task<Results<Ok<ProfileResponse>, ValidationProblem>> Put(
        ClaimsPrincipal principal,
        UpdateProfileRequest request,
        ITutorProfileRepository repo,
        TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZoneId)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId.Trim(), out var timeZone))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["TimeZoneId"] = ["A valid IANA time zone id is required (e.g. \"Europe/Kyiv\")."],
            });

        var profile = await repo.GetAsync(principal.GetTelegramId());
        if (profile is null)
        {
            profile = TutorProfile.Create(principal.GetTelegramId(), timeZone, clock.GetUtcNow());
            await repo.AddAsync(profile);
        }
        else
        {
            profile.UpdateTimeZone(timeZone);
            await repo.UpdateAsync(profile);
        }

        return TypedResults.Ok(ProfileResponse.From(profile));
    }
}
