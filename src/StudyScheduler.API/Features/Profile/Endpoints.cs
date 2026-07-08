using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.API.Core.Time;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
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

        // Devices may detect a renamed spelling the host tzdata lacks ("Europe/Kyiv" vs
        // "Europe/Kiev") — advertise both twins, TryResolve accepts either.
        return IanaTimeZone.WithRenameTwins(ids.Distinct().ToList())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
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
    /// each other. Changing the zone moves the active series anchored in the old profile zone
    /// along with it (wall-clock preserved); series anchored elsewhere keep their own zone.
    /// </summary>
    public static async Task<Results<Ok<ProfileResponse>, ValidationProblem>> Put(
        ClaimsPrincipal principal,
        UpdateProfileRequest request,
        ITutorProfileRepository repo,
        ILessonSeriesRepository seriesRepo,
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!IanaTimeZone.TryResolve(request.TimeZoneId, out var timeZone))
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

        // 0 disables reminders; null leaves the stored setting unchanged (see the contract docs).
        if (request.RemindMinutes is { } remind and not 0
            && remind is < TutorProfile.MinRemindMinutes or > TutorProfile.MaxRemindMinutes)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["RemindMinutes"] = [
                    $"Reminder lead time must be 0 (off) or between {TutorProfile.MinRemindMinutes} and {TutorProfile.MaxRemindMinutes} minutes."],
            });

        // The pre-checks above own the HTTP payload wording, so the domain calls below cannot
        // legitimately fail — their Results are still honored (mapped to 400) rather than
        // discarded, so a drift between the two layers can't slip through silently.
        var profile = await repo.GetAsync(principal.GetTelegramId(), ct);
        if (profile is null)
        {
            var created = TutorProfile.Create(principal.GetTelegramId(), timeZone, clock.GetUtcNow(), languageCode);
            if (!created.IsSuccess)
                return created.ToValidationProblem();
            profile = created.Value;
            if (ApplyNotificationSettings(profile, request) is { IsSuccess: false } settingsFailure)
                return settingsFailure.ToValidationProblem();
            repo.Add(profile);
        }
        else
        {
            var previousZone = profile.TimeZone;
            profile.UpdateTimeZone(timeZone);
            if (languageCode is not null
                && profile.UpdateLanguage(languageCode) is { IsSuccess: false } languageFailure)
                return languageFailure.ToValidationProblem();
            if (ApplyNotificationSettings(profile, request) is { IsSuccess: false } settingsFailure)
                return settingsFailure.ToValidationProblem();
            repo.Update(profile);

            if (previousZone.Id != timeZone.Id)
            {
                foreach (var series in await seriesRepo.GetActiveByTimeZoneAsync(
                    principal.GetTelegramId(), previousZone, ct))
                {
                    series.MoveToTimeZone(timeZone);
                    seriesRepo.Update(series);
                }
            }
        }

        // One commit for the profile and the rebased series — a zone change either moves
        // everything or nothing, never a profile pointing at a zone its series don't share.
        await uow.SaveChangesAsync(ct);
        return TypedResults.Ok(ProfileResponse.From(profile));
    }

    private static Result ApplyNotificationSettings(TutorProfile profile, UpdateProfileRequest request)
    {
        if (request.RemindMinutes is { } remind)
        {
            // 0 (off) maps to the domain's null before the range check re-runs there.
            var updated = profile.UpdateRemindMinutes(remind == 0 ? null : remind);
            if (!updated.IsSuccess)
                return updated;
        }

        if (request.NotifyAfterLesson is { } notifyAfter)
            profile.UpdateNotifyAfterLesson(notifyAfter);
        return Result.Success();
    }
}
