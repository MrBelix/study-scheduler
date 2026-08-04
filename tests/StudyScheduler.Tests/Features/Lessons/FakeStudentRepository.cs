using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// In-memory <see cref="IStudentRepository"/> mirroring the EF semantics, tenancy included — see
/// <see cref="FakeLessonRepository"/> for how the filter and the insert stamp are modelled.
/// </summary>
internal sealed class FakeStudentRepository(ITutorContext tutor) : IStudentRepository
{
    public List<Student> Items { get; } = [];

    private IEnumerable<Student> Mine =>
        Items.Where(s => s.TutorTelegramId == (tutor.CurrentTutorTelegramId ?? 0));

    public Task<Student?> GetByIdAsync(Guid id, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Mine.SingleOrDefault(s => s.Id == id));

    public Task<List<Student>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult(Mine.Where(s => ids.Contains(s.Id)).ToList());

    public Task<List<Student>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult(Mine.ToList());

    public Task<List<Student>> GetByStatusAsync(StudentStatus status, CancellationToken ct = default) =>
        Task.FromResult(Mine.Where(s => s.Status == status).ToList());

    public void Add(Student student) => Items.Add(TenantStamp.Apply(student, tutor));

    public void Update(Student student) { }
}
