using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.API.Core.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>: one commit per scope over the same
/// <see cref="AppDbContext"/> the repositories stage into.
/// </summary>
public sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public void DiscardChanges() => db.ChangeTracker.Clear();
}
