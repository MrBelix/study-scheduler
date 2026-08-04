using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Reports;

/// <summary>
/// One student's outstanding balance: lessons already taught (<see cref="LessonStatus.Completed"/>)
/// that were never paid for. Cancelled lessons owe nothing and scheduled ones are not owed yet, so
/// neither can be a debt.
/// </summary>
public sealed record StudentDebt(Guid StudentId, decimal Amount, int LessonsCount, DateTimeOffset OldestUtc);

/// <summary>
/// Reads the current tutor's debt ledger. Its own seam inside the Reports slice rather than another
/// method on <see cref="ILessonRepository"/>: the shape is a reporting projection nothing else
/// consumes, and unit tests need it substitutable without a database.
/// </summary>
public interface IStudentDebtReader
{
    /// <summary>
    /// Per student, the unpaid completed lessons over the tutor's whole history. Deliberately not
    /// range-bounded: a debt does not stop being owed because the reporting period moved on.
    /// </summary>
    Task<IReadOnlyList<StudentDebt>> GetAllTimeAsync(CancellationToken ct = default);
}

/// <summary>EF Core implementation of <see cref="IStudentDebtReader"/> (PostgreSQL).</summary>
public sealed class EfStudentDebtReader(AppDbContext db) : IStudentDebtReader
{
    public async Task<IReadOnlyList<StudentDebt>> GetAllTimeAsync(CancellationToken ct = default)
    {
        // One round trip for the whole ledger, narrowed to the three columns the projection needs.
        // Whose ledger it is comes from the context's tenancy filter, not from a predicate here, and
        // WHICH rows are money owed comes from the domain's own definition — the same expression one
        // student's debt page reads through, so the two can never drift apart.
        // The grouping runs in memory rather than as SQL GROUP BY because StartUtc goes through a
        // value converter, which MIN() cannot translate (same reason
        // EfLessonRepository.GetStudentStatsAsync avoids it) — and only a tutor's unpaid completed
        // lessons ever travel, which is a small, self-limiting set the tutor is actively chasing.
        var unpaid = await db.Lessons
            .AsNoTracking()
            .Where(Lesson.IsDebt)
            .Select(l => new { l.StudentId, l.Price, l.StartUtc })
            .ToListAsync(ct);

        return unpaid
            .GroupBy(l => l.StudentId)
            .Select(g => new StudentDebt(g.Key, g.Sum(l => l.Price), g.Count(), g.Min(l => l.StartUtc)))
            .ToList();
    }
}
