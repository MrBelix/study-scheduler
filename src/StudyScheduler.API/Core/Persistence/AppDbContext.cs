using Microsoft.EntityFrameworkCore;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Core.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Student> Students => Set<Student>();

    public DbSet<TutorProfile> TutorProfiles => Set<TutorProfile>();

    public DbSet<LessonSeries> LessonSeries => Set<LessonSeries>();

    public DbSet<Lesson> Lessons => Set<Lesson>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
