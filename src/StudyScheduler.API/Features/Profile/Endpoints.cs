using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>HTTP handlers for the Profile feature. Wired to routes in <see cref="ProfileModule"/>.</summary>
internal static class Endpoints
{
    /// <summary>
    /// IANA ids only, whatever the host OS. On Linux (production) the system list is IANA already;
    /// on Windows (local dev) it enumerates Windows ids ("FLE Standard Time"), which
    /// <see cref="Put"/> would reject — so each is converted, and the few legacy zones with no
    /// IANA mapping at all ("Mid-Atlantic Standard Time") are dropped. Computed once; the set only
    /// changes with an OS tzdata update, which implies a process restart anyway.
    /// </summary>
    private static readonly Lazy<List<string>> IanaTimeZoneIds = new(() =>
    {
        var ids = TimeZoneInfo.GetSystemTimeZones().Select(tz => tz.Id);
        if (OperatingSystem.IsWindows())
            ids = ids
                .Select(id => TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) ? ianaId : null)
                .OfType<string>();

        return ids.Distinct().Order(StringComparer.Ordinal).ToList();
    });

    /// <summary>
    /// Lists the IANA time zone ids accepted by <c>PUT /profile</c>, for the client's zone picker.
    /// </summary>
    public static Ok<List<string>> GetTimeZones() =>
        TypedResults.Ok(IanaTimeZoneIds.Value);

    /// <summary>Returns the current tutor's profile, 404 until it is first saved.</summary>
    public static async Task<Results<Ok<ProfileResponse>, NotFound>> Get(
        ClaimsPrincipal principal,
        ITutorProfileRepository repo,
        CancellationToken ct)
    {
        var profile = await repo.GetAsync(principal.GetTelegramId(), ct);
        return profile is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ProfileResponse.From(profile));
    }

    /// <summary>
    /// Creates or updates the current tutor's profile (upsert). <c>LanguageCode</c> null leaves
    /// the stored language untouched, so time-zone-only and language-only saves don't clobber
    /// each other.
    /// </summary>
    public static async Task<Results<Ok<ProfileResponse>, ValidationProblem>> Put(
        ClaimsPrincipal principal,
        UpdateProfileRequest request,
        ITutorProfileRepository repo,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZoneId)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId.Trim(), out var timeZone))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["TimeZoneId"] = ["A valid IANA time zone id is required (e.g. \"Europe/Kyiv\")."],
            });

        // Two-letter ISO 639-1, case-insensitive ("UK" → "uk"). Deliberately not a hard
        // uk/en whitelist: a new client locale must not require a backend release.
        var languageCode = request.LanguageCode?.Trim().ToLowerInvariant();
        if (languageCode is not null
            && (languageCode.Length != 2 || !languageCode.All(char.IsAsciiLetterLower)))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["LanguageCode"] = ["A two-letter ISO 639-1 language code is required (e.g. \"uk\")."],
            });

        var profile = await repo.GetAsync(principal.GetTelegramId(), ct);
        if (profile is null)
        {
            profile = TutorProfile.Create(principal.GetTelegramId(), timeZone, clock.GetUtcNow(), languageCode);
            await repo.AddAsync(profile, ct);
        }
        else
        {
            profile.UpdateTimeZone(timeZone);
            if (languageCode is not null)
                profile.UpdateLanguage(languageCode);
            await repo.UpdateAsync(profile, ct);
        }

        return TypedResults.Ok(ProfileResponse.From(profile));
    }
}
