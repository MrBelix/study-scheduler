using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StudyScheduler.API.Core.Persistence;

/// <summary>
/// Persists <see cref="TimeZoneInfo"/> properties as their IANA id string (e.g. "Europe/Kyiv").
/// </summary>
internal static class TimeZoneInfoConversion
{
    // Typed as the non-generic bases so PropertyBuilder.HasConversion picks its non-generic
    // overload — the generic one trips CS8620 between TimeZoneInfo and TimeZoneInfo? call sites.
    public static readonly ValueConverter Converter =
        new ValueConverter<TimeZoneInfo, string>(
            timeZone => timeZone.Id,
            id => TimeZoneInfo.FindSystemTimeZoneById(id));

    // TimeZoneInfo is a reference type without value equality; compare by id so EF change
    // detection doesn't see every re-resolved instance as a modification.
    public static readonly ValueComparer Comparer =
        new ValueComparer<TimeZoneInfo>(
            (a, b) => (a == null ? null : a.Id) == (b == null ? null : b.Id),
            timeZone => timeZone.Id.GetHashCode(),
            timeZone => timeZone);

    /// <summary>Covers both required (<c>TimeZoneInfo</c>) and optional (<c>TimeZoneInfo?</c>) properties.</summary>
    public static PropertyBuilder<T> HasTimeZoneConversion<T>(this PropertyBuilder<T> property) =>
        property
            .HasConversion(Converter, Comparer)
            .HasMaxLength(100);

    /// <summary>Same conversion for a property mapped inside a complex (value-object) type.</summary>
    public static ComplexTypePropertyBuilder<T> HasTimeZoneConversion<T>(this ComplexTypePropertyBuilder<T> property) =>
        property
            .HasConversion(Converter, Comparer)
            .HasMaxLength(100);
}
