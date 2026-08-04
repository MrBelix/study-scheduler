using StudyScheduler.API.Features.Reports;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Tests.Features.Lessons;

namespace StudyScheduler.Tests.Features.Reports;

/// <summary>
/// In-memory <see cref="IStudentDebtReader"/> mirroring the EF query semantics — it reads the same
/// lesson store the schedule does, through the same tenant filter, so "the debt ledger ignores the
/// reporting period" is an assertion about behaviour rather than about a hand-fed list.
/// </summary>
internal sealed class FakeStudentDebtReader(FakeLessonRepository lessons) : IStudentDebtReader
{
    public Task<IReadOnlyList<StudentDebt>> GetAllTimeAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StudentDebt>>(lessons.Mine
            .Where(l => l.Status == LessonStatus.Completed && !l.IsPaid)
            .GroupBy(l => l.StudentId)
            .Select(g => new StudentDebt(g.Key, g.Sum(l => l.Price), g.Count(), g.Min(l => l.StartUtc)))
            .ToList());
}
