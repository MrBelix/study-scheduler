using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        builder.ToTable("Lessons");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.TutorTelegramId).IsRequired();
        // Range reads ("my schedule between X and Y") filter by tutor then by start.
        builder.HasIndex(l => new { l.TutorTelegramId, l.StartUtc });

        builder.Property(l => l.StudentId).IsRequired();

        builder.Property(l => l.SeriesId);
        builder.Property(l => l.OccurrenceDate);

        // One physical row per materialized series slot. Filtered so one-off lessons
        // (SeriesId null) don't collide on a shared NULL key — SQL Server treats NULLs as equal
        // in a unique index otherwise.
        builder.HasIndex(l => new { l.SeriesId, l.OccurrenceDate })
            .IsUnique()
            .HasFilter("[SeriesId] IS NOT NULL");

        builder.Property(l => l.StartUtc).IsRequired();
        builder.Property(l => l.EndUtc).IsRequired();
        builder.Property(l => l.DurationMinutes).IsRequired();

        builder.Property(l => l.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(l => l.Price).HasPrecision(18, 2);
        builder.Property(l => l.IsPaid).IsRequired();

        builder.Property(l => l.Topic).HasMaxLength(Lesson.MaxTopicLength);
        builder.Property(l => l.Description).HasMaxLength(Lesson.MaxDescriptionLength);

        // Per-lesson notification dedup stored inline as flat, nullable columns.
        builder.ComplexProperty(l => l.Notifications, n =>
        {
            n.Property(x => x.ReminderSentAtUtc).HasColumnName("ReminderSentAtUtc");
            n.Property(x => x.FollowUpSentAtUtc).HasColumnName("FollowUpSentAtUtc");
        });

        builder.Property(l => l.CreatedAtUtc);
    }
}
