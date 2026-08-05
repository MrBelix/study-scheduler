using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.API.Core.Tenancy;
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
        ITutorProfileRepository repo,
        ILessonRepository lessons,
        TimeProvider clock,
        CancellationToken ct)
    {
        var profile = await repo.GetAsync(ct);
        if (profile is null)
            return TypedResults.NotFound();

        var tomorrowLessonsCount = await CountTomorrowAsync(profile, lessons, clock, ct);
        return TypedResults.Ok(ProfileResponse.From(profile, tomorrowLessonsCount));
    }

    /// <summary>
    /// Creates or updates the current tutor's profile (upsert). <c>LanguageCode</c> null leaves
    /// the stored language untouched, so time-zone-only and language-only saves don't clobber
    /// each other.
    /// </summary>
    public static async Task<Results<Ok<ProfileResponse>, ValidationProblem>> Put(
        UpdateProfileRequest request,
        ITutorProfileRepository repo,
        ITutorContext tutor,
        IUnitOfWork uow,
        ILessonRepository lessons,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!IanaTimeZone.TryResolve(request.TimeZoneId, out var timeZone))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["TimeZoneId"] = ["A valid IANA time zone id is required (e.g. \"Europe/Kyiv\")."],
            });

        // The DTO stays a nullable string, same reasoning as the time zone above: a malformed
        // value yields the clean ValidationProblem here rather than a JSON-binding 400.
        TimeOnly? morningAgendaAt = null;
        if (request.MorningAgendaAt is not null)
        {
            if (!TimeOnly.TryParseExact(
                    request.MorningAgendaAt, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["MorningAgendaAt"] = ["A time of day in HH:mm format is required (e.g. \"08:00\")."],
                });
            morningAgendaAt = at;
        }

        // The DTO stays a nullable string so an unknown value yields the clean ValidationProblem
        // below rather than a JSON-binding 400. Parse it once, here, through the domain's single
        // source of truth: null means "leave the language unchanged" (see the contract docs).
        AppLanguage? languageCode = null;
        if (request.LanguageCode is not null)
        {
            var parsedLanguage = AppLanguageCode.ParseCode(request.LanguageCode);
            if (!parsedLanguage.IsSuccess)
                return parsedLanguage.ToValidationProblem();
            languageCode = parsedLanguage.Value;
        }

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
        var profile = await repo.GetAsync(ct);
        if (profile is null)
        {
            // The only place tenancy is still spelled out, and it has to be: a profile is KEYED by
            // the tutor id, so the tenant is this row's identity rather than a column stamped onto
            // it. The route requires authentication, so the scope always has one.
            var tutorTelegramId = tutor.CurrentTutorTelegramId
                ?? throw new InvalidOperationException("No tutor is established for this request.");

            var created = TutorProfile.Create(tutorTelegramId, timeZone, clock.GetUtcNow(), languageCode);
            if (!created.IsSuccess)
                return created.ToValidationProblem();
            profile = created.Value;
            if (ApplyNotificationSettings(profile, request, morningAgendaAt) is { IsSuccess: false } settingsFailure)
                return settingsFailure.ToValidationProblem();
            repo.Add(profile);
        }
        else
        {
            profile.UpdateTimeZone(timeZone);
            if (languageCode is { } language)
                profile.UpdateLanguage(language);
            if (ApplyNotificationSettings(profile, request, morningAgendaAt) is { IsSuccess: false } settingsFailure)
                return settingsFailure.ToValidationProblem();
            repo.Update(profile);
        }

        await uow.SaveChangesAsync(ct);
        var tomorrowLessonsCount = await CountTomorrowAsync(profile, lessons, clock, ct);
        return TypedResults.Ok(ProfileResponse.From(profile, tomorrowLessonsCount));
    }

    private static Result ApplyNotificationSettings(
        TutorProfile profile, UpdateProfileRequest request, TimeOnly? morningAgendaAt)
    {
        if (request.RemindMinutes is { } remind)
        {
            // 0 (off) maps to the domain's null before the range check re-runs there.
            var updated = profile.UpdateRemindMinutes(remind == 0 ? null : remind);
            if (!updated.IsSuccess)
                return updated;
        }

        if (request.DaySummary is { } daySummary)
            profile.UpdateDaySummary(daySummary);

        if (request.MorningAgenda is { } morningAgenda)
            profile.UpdateMorningAgenda(morningAgenda);

        // Never gated on MorningAgenda: the client hides the row when the toggle is off, and the
        // stored time must survive the toggle being flipped off and back on.
        if (morningAgendaAt is { } at)
            profile.UpdateMorningAgendaAt(at);

        return Result.Success();
    }

    /// <summary>
    /// Non-cancelled lessons whose local start date (in the tutor's zone) is tomorrow — the hint the
    /// agenda-time bottom sheet shows. <see cref="ILessonRepository.GetInRangeAsync"/> intersects the
    /// UTC day window, so a lesson spilling in from today is filtered back out: a lesson belongs to
    /// the day it starts on.
    /// </summary>
    private static async Task<int> CountTomorrowAsync(
        TutorProfile profile, ILessonRepository lessons, TimeProvider clock, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), profile.TimeZone).DateTime);
        var tomorrow = today.AddDays(1);
        var from = WallClock.ToUtc(tomorrow, TimeOnly.MinValue, profile.TimeZone);
        var to = WallClock.ToUtc(tomorrow.AddDays(1), TimeOnly.MinValue, profile.TimeZone);

        var schedule = await lessons.GetInRangeAsync(from, to, ct: ct);
        return schedule.Count(l =>
            l.Status != LessonStatus.Cancelled
            && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(l.StartUtc, profile.TimeZone).DateTime) == tomorrow);
    }
}
