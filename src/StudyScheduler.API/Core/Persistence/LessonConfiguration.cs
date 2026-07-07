using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        builder.ToTable("Lessons");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.TutorTelegramId).IsRequired();
        // Ownership / scope key + start: serves both range lists and overlap scans.
        builder.HasIndex(l => new { l.TutorTelegramId, l.StartUtc });

        // Strict physical-row → virtual-slot mapping: a series slot (identified by its canonical
        // local date) can only be materialized once, even under concurrent mutations.
        builder.HasIndex(l => new { l.SeriesId, l.OccurrenceDate })
            .IsUnique()
            .HasFilter("[SeriesId] IS NOT NULL");

        builder.Property(l => l.Price).HasPrecision(18, 2);

        builder.Property(l => l.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(l => l.Topic).HasMaxLength(Lesson.MaxTopicLength);
        builder.Property(l => l.Description).HasMaxLength(Lesson.MaxDescriptionLength);
        builder.Property(l => l.CreatedAtUtc);

        // FKs without navigation properties — aggregates stay decoupled. Restrict is safe:
        // students are archived and series deactivated, never deleted.
        builder.HasOne<Student>()
            .WithMany()
            .HasForeignKey(l => l.StudentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<LessonSeries>()
            .WithMany()
            .HasForeignKey(l => l.SeriesId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
