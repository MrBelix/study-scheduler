namespace StudyScheduler.Domain.Primitives;

/// <summary>
/// Commits everything staged through the repositories as one atomic unit. Repositories only
/// stage changes (<c>Add</c>/<c>Update</c>); nothing reaches the database until
/// <see cref="SaveChangesAsync"/> — so a handler that touches several aggregates
/// (archive student → end series, move profile zone → rebase series) commits all or nothing.
/// </summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Discards everything staged but not yet saved. Recovery hook after a failed save (e.g.
    /// losing a unique-index race): without it the failed entity stays staged and every later
    /// save in the same scope retries the doomed write.
    /// </summary>
    void DiscardChanges();
}
