namespace StudyScheduler.API.Core.Tenancy;

/// <summary>
/// The tutor everything in the current scope belongs to — the read side of tenancy. Scoped: one
/// value per HTTP request or per background unit of work, which is what lets
/// <see cref="Persistence.AppDbContext"/> filter every query by it without a single caller passing
/// an id.
/// </summary>
public interface ITutorContext
{
    /// <summary>
    /// Telegram id of the tutor owning this scope, or <c>null</c> when nothing has established one —
    /// an anonymous request, or a background scope before it picks a tenant. A null tenant reads
    /// NOTHING (the filters match no row) rather than everything: tenancy fails closed.
    /// </summary>
    long? CurrentTutorTelegramId { get; }
}
