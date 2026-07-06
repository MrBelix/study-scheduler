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

        // Makes lazy materialization idempotent: a series occurrence (identified by its canonical
        // local date) can only be inserted once, even under concurrent GETs.
        builder.HasIndex(l => new { l.SeriesId, l.OccurrenceDate })
            .IsUnique()
            .HasFilter("[SeriesId] IS NOT NULL");

        builder.Property(l => l.Price).HasPrecision(18, 2);

        builder.Property(l => l.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(l => l.Topic).HasMaxLength(1000);
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
