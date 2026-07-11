using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Tutors;

/// <summary>
/// The tutor's chosen UI language. Deliberately a closed set: the bot and the frontend both only
/// speak Ukrainian and English, so a code outside this set is a genuine validation failure, not a
/// locale the backend should silently pass through. "Not chosen yet" is modeled by a nullable
/// <see cref="AppLanguage"/> rather than a member.
/// </summary>
public enum AppLanguage
{
    Uk,
    En,
}

/// <summary>
/// Single source of truth for the wire/DB representation of <see cref="AppLanguage"/> — the
/// lower-case ISO 639-1 code ("uk"/"en") the frontend sends and the column stores. Kept in Domain
/// so the API boundary, the EF value conversion and the tests all round-trip through the same map.
/// </summary>
public static class AppLanguageCode
{
    // Reused verbatim by the API mapping and the EF read converter, so the error contract stays
    // identical everywhere an unsupported code is rejected.
    private static readonly Error InvalidLanguageCode = new(
        "Profile.InvalidLanguageCode",
        "Language code must be a two-letter ISO 639-1 code.",
        "LanguageCode");

    public static string ToCode(this AppLanguage language) => language switch
    {
        AppLanguage.Uk => "uk",
        AppLanguage.En => "en",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>
    /// Parses a client/stored language code: trimmed and case-insensitive ("EN", " uk " accepted).
    /// Anything that is not a supported code — wrong length, non-letters, or a valid-format but
    /// unsupported code like "fr" — fails with <c>Profile.InvalidLanguageCode</c>.
    /// </summary>
    public static Result<AppLanguage> ParseCode(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "uk" => Result<AppLanguage>.Success(AppLanguage.Uk),
            "en" => Result<AppLanguage>.Success(AppLanguage.En),
            _ => Result<AppLanguage>.Failure(InvalidLanguageCode),
        };
}
