using System.Globalization;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Profile;

/// <summary>
/// Request body for creating/updating the current tutor's profile (upsert). Null fields mean
/// "leave unchanged", so partial saves don't clobber each other: <c>LanguageCode</c> null keeps
/// the language, <c>RemindMinutes</c> null keeps the reminder setting (<b>0 turns reminders
/// off</b> — null can't, or every timezone-only save would disable them), <c>DaySummary</c>
/// null keeps the evening summary setting, <c>MorningAgenda</c> null keeps the morning agenda
/// setting, <c>MorningAgendaAt</c> null keeps the stored send time ("HH:mm", e.g. "08:00") — it
/// is never gated on <c>MorningAgenda</c>, so a stored time survives the toggle being flipped
/// off and back on.
/// </summary>
public sealed record UpdateProfileRequest(
    string TimeZoneId,
    string? LanguageCode,
    int? RemindMinutes = null,
    bool? DaySummary = null,
    bool? MorningAgenda = null,
    string? MorningAgendaAt = null);

/// <summary>
/// Tutor profile projection returned to the client. <c>RemindMinutes</c> null — reminders off.
/// <c>MorningAgendaAt</c> is a wall-clock "HH:mm" string in the tutor's own zone.
/// <c>TomorrowLessonsCount</c> is the number of non-cancelled lessons starting tomorrow (local),
/// the hint the agenda-time bottom sheet shows. <c>BotReachable</c> false means the bot was
/// blocked/never-started (a 403 disabled it); the client prompts the tutor to reopen the bot to
/// resume notifications.
/// </summary>
public sealed record ProfileResponse(
    string TimeZoneId,
    string? LanguageCode,
    int? RemindMinutes,
    bool DaySummary,
    bool MorningAgenda,
    string MorningAgendaAt,
    bool BotReachable,
    int TomorrowLessonsCount,
    DateTimeOffset CreatedAtUtc)
{
    public static ProfileResponse From(TutorProfile profile, int tomorrowLessonsCount) => new(
        profile.TimeZone.Id,
        // Serialize the enum back to its lowercase wire code ("uk"/"en"); null stays null.
        profile.LanguageCode?.ToCode(),
        profile.RemindMinutes,
        profile.DaySummary,
        profile.MorningAgenda,
        profile.MorningAgendaAtLocal.ToString("HH\\:mm", CultureInfo.InvariantCulture),
        profile.BotReachable,
        tomorrowLessonsCount,
        profile.CreatedAtUtc);
}
