namespace StudyScheduler.Domain.Lessons;

/// <summary>Persistence contract for <see cref="LessonSeries"/>.</summary>
public interface ILessonSeriesRepository
{
    Task<LessonSeries?> GetByIdAsync(Guid id);

    Task<List<LessonSeries>> GetActiveByTutorAsync(long tutorTelegramId);

    Task<List<LessonSeries>> GetAllByTutorAsync(long tutorTelegramId);

    Task AddAsync(LessonSeries series);

    Task UpdateAsync(LessonSeries series);
}
