using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StudyScheduler.API.Core.Persistence;

/// <summary>
/// Normalizes every <see cref="DateTimeOffset"/> property to a UTC instant before it reaches the
/// provider. Npgsql maps <see cref="DateTimeOffset"/> to <c>timestamp with time zone</c>
/// (timestamptz), which stores a plain instant and therefore rejects any value whose offset isn't
/// zero — and the API binds instants straight from JSON, where a client may legitimately send
/// "+03:00". Converting to a <see cref="DateTimeKind.Utc"/> <see cref="DateTime"/> on write (and
/// back to a zero-offset <see cref="DateTimeOffset"/> on read) enforces the domain's "time is UTC"
/// rule at the storage boundary instead of scattering <c>ToUniversalTime()</c> across call sites.
/// </summary>
/// <remarks>
/// The model has no local wall-clock <see cref="DateTime"/> columns, so nothing needs
/// <c>timestamp without time zone</c>: a series' wall clock is a <see cref="TimeOnly"/> plus its
/// IANA zone id, and occurrence/series dates are <see cref="DateOnly"/>.
/// </remarks>
internal sealed class UtcTimestampConversion : ValueConverter<DateTimeOffset, DateTime>
{
    public UtcTimestampConversion()
        : base(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
    {
    }
}
