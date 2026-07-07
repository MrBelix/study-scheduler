using System.Diagnostics.CodeAnalysis;

namespace StudyScheduler.API.Core.Time;

/// <summary>
/// Resolves IANA time zone ids, tolerating tz database renames the host may lag behind on:
/// a device's ICU can send "Europe/Kyiv" while the host tzdata still only knows "Europe/Kiev"
/// (or the reverse). Every consumer of a client-supplied time zone id goes through
/// <see cref="TryResolve"/> instead of <see cref="TimeZoneInfo.TryFindSystemTimeZoneById"/>.
/// </summary>
public static class IanaTimeZone
{
    /// <summary>Canonical renames in the tz database; either spelling may be unknown to the host.</summary>
    private static readonly string[][] RenamedPairs =
    [
        ["Europe/Kyiv", "Europe/Kiev"],
        ["America/Nuuk", "America/Godthab"],
        ["Asia/Yangon", "Asia/Rangoon"],
        ["Asia/Kolkata", "Asia/Calcutta"],
        ["Asia/Ho_Chi_Minh", "Asia/Saigon"],
    ];

    private static readonly Dictionary<string, string> RenameTwin = BuildTwinLookup();

    /// <summary>
    /// Resolves <paramref name="id"/> to a system time zone, falling back to the rename twin
    /// ("Europe/Kyiv" → "Europe/Kiev") when the host doesn't know the requested spelling.
    /// The returned zone carries the id the host resolved, which may be the twin's.
    /// </summary>
    public static bool TryResolve(string? id, [NotNullWhen(true)] out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var trimmed = id.Trim();
        if (TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out timeZone))
            return true;

        return RenameTwin.TryGetValue(trimmed, out var twin)
            && TimeZoneInfo.TryFindSystemTimeZoneById(twin, out timeZone);
    }

    /// <summary>
    /// Adds the missing spelling of each renamed pair to <paramref name="ids"/> when its twin is
    /// present — so the advertised list always contains the modern name devices detect, even on
    /// hosts whose tzdata predates the rename (and vice versa).
    /// </summary>
    public static IEnumerable<string> WithRenameTwins(IReadOnlyCollection<string> ids)
    {
        foreach (var id in ids)
            yield return id;

        foreach (var pair in RenamedPairs)
        {
            if (ids.Contains(pair[0]) != ids.Contains(pair[1]))
                yield return ids.Contains(pair[0]) ? pair[1] : pair[0];
        }
    }

    private static Dictionary<string, string> BuildTwinLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in RenamedPairs)
        {
            lookup[pair[0]] = pair[1];
            lookup[pair[1]] = pair[0];
        }

        return lookup;
    }
}
