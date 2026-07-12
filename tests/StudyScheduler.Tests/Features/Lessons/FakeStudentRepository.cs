using StudyScheduler.Domain.Students;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>In-memory <see cref="IStudentRepository"/> for scheduling tests (rate resolution).</summary>
internal sealed class FakeStudentRepository : IStudentRepository
{
    public List<Student> Items { get; } = [];

    public Task<Student?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.Id == id && s.TutorTelegramId == tutorTelegramId));

    public Task<List<Student>> GetByIdsAsync(long tutorTelegramId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(s => s.TutorTelegramId == tutorTelegramId && ids.Contains(s.Id)).ToList());

    public Task<List<Student>> GetAllByTutorIdAsync(long tutorTelegramId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(s => s.TutorTelegramId == tutorTelegramId).ToList());

    public void Add(Student student) => Items.Add(student);

    public void Update(Student student) { }
}
