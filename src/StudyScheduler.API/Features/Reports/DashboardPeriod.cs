using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Reports;

/// <summary>The reporting granularities the Money dashboard can be asked for.</summary>
public enum DashboardPeriodKind
{
    Week,
    Month,
    Quarter,
}

/// <summary>
/// The dashboard's reporting window as inclusive local dates in the tutor's own time zone, plus the
/// geometry derived from it: the comparison baseline (<see cref="Previous"/>), the UTC window the
/// schedule is read over (<see cref="ToUtcWindow"/>) and the chart buckets
/// (<see cref="SplitIntoBuckets"/>). Everything here is pure — the whole period arithmetic is
/// decided without touching the database, which is also how the tests drive it.
/// </summary>
public sealed record DashboardPeriod(DashboardPeriodKind Kind, DateOnly From, DateOnly To)
{
    /// <summary>
    /// Parses the <c>period</c> query value; <c>null</c> when it names no known granularity, which
    /// the endpoint turns into a 400. Deliberately not <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>:
    /// that also accepts numeric strings ("0"), which the documented contract does not.
    /// </summary>
    public static DashboardPeriodKind? ParseKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "week" => DashboardPeriodKind.Week,
        "month" => DashboardPeriodKind.Month,
        "quarter" => DashboardPeriodKind.Quarter,
        _ => null,
    };

    /// <summary>
    /// The window of <paramref name="kind"/> containing <paramref name="anchor"/> — any date inside
    /// a window resolves to the same window, so the client can anchor on whatever day it is showing.
    /// Weeks run Monday to Sunday.
    /// </summary>
    public static DashboardPeriod Resolve(DashboardPeriodKind kind, DateOnly anchor)
    {
        switch (kind)
        {
            case DashboardPeriodKind.Week:
                var monday = MondayOf(anchor);
                return new DashboardPeriod(kind, monday, monday.AddDays(6));

            case DashboardPeriodKind.Month:
                var firstOfMonth = new DateOnly(anchor.Year, anchor.Month, 1);
                return new DashboardPeriod(kind, firstOfMonth, firstOfMonth.AddMonths(1).AddDays(-1));

            case DashboardPeriodKind.Quarter:
                var firstOfQuarter = new DateOnly(anchor.Year, (((anchor.Month - 1) / 3) * 3) + 1, 1);
                return new DashboardPeriod(kind, firstOfQuarter, firstOfQuarter.AddMonths(3).AddDays(-1));

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled dashboard period kind.");
        }
    }

    /// <summary>
    /// The window of the same kind immediately before this one — the baseline behind
    /// <c>income.previous</c>. Anchoring one step back on the calendar (not "minus N days") keeps
    /// month and quarter comparisons aligned to their own uneven lengths.
    /// </summary>
    public DashboardPeriod Previous => Kind switch
    {
        DashboardPeriodKind.Week => Resolve(Kind, From.AddDays(-7)),
        DashboardPeriodKind.Month => Resolve(Kind, From.AddMonths(-1)),
        DashboardPeriodKind.Quarter => Resolve(Kind, From.AddMonths(-3)),
        _ => throw new InvalidOperationException($"Unhandled dashboard period kind '{Kind}'."),
    };

    /// <summary>Whole days covered, both bounds included.</summary>
    public int DayCount => To.DayNumber - From.DayNumber + 1;

    /// <summary>
    /// The half-open UTC window <c>[From 00:00 local, day-after-To 00:00 local)</c> — the shape
    /// the lesson range query reads, resolved through the same DST-correct
    /// <see cref="WallClock"/> seam a series' occurrences go through, so a period boundary and a
    /// lesson time can never disagree about where local midnight is.
    /// </summary>
    public (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ToUtcWindow(TimeZoneInfo zone) => (
        WallClock.ToUtc(From, TimeOnly.MinValue, zone),
        WallClock.ToUtc(To.AddDays(1), TimeOnly.MinValue, zone));

    /// <summary>
    /// The chart buckets covering the period without gaps or overlaps: one per day for a week, one
    /// per Monday-based calendar week otherwise — clipped to the period, so the first and the last
    /// bucket of a month or quarter are usually partial weeks.
    /// </summary>
    public IReadOnlyList<(DateOnly From, DateOnly To)> SplitIntoBuckets()
    {
        var buckets = new List<(DateOnly From, DateOnly To)>();
        for (var cursor = From; cursor <= To;)
        {
            var end = Kind == DashboardPeriodKind.Week ? cursor : MondayOf(cursor).AddDays(6);
            if (end > To)
                end = To;

            buckets.Add((cursor, end));
            cursor = end.AddDays(1);
        }

        return buckets;
    }

    /// <summary>The Monday of the ISO week containing <paramref name="date"/>.</summary>
    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
