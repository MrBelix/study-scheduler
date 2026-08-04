using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Tests.Core.Tenancy;

/// <summary>
/// Gives a fixture row an owner the way persistence does. Nothing above the database assigns
/// ownership any more — <c>AppDbContext</c> stamps it through EF's property entry as the row is
/// inserted, and the fake repositories stamp it on <c>Add</c> — so a test that needs a row belonging
/// to SOMEBODY ELSE, or a row that must exist before any tenant is established, writes it here
/// instead, playing exactly the part EF plays. Test-only: the domain deliberately exposes no setter.
/// </summary>
internal static class TenantOwnership
{
    /// <summary>The same entity, owned by <paramref name="tutorTelegramId"/>.</summary>
    public static T OwnedBy<T>(this T entity, long tutorTelegramId)
        where T : ITutorOwned
    {
        entity!.GetType()
            .GetProperty(nameof(ITutorOwned.TutorTelegramId))!
            .SetValue(entity, tutorTelegramId);
        return entity;
    }
}
