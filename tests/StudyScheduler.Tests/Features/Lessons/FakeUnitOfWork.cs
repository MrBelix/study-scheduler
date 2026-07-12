using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>No-op <see cref="IUnitOfWork"/> for pipeline unit tests (the fake repos hold state).</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    public void DiscardChanges() { }
}
