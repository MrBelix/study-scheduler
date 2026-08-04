using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class LessonSeriesConfiguration : IEntityTypeConfiguration<LessonSeries>
{
    public void Configure(EntityTypeBuilder<LessonSeries> builder)
    {
        // Ownership is positive by construction (a Telegram id is), and the database says so: 0 is
        // the "no tenant" sentinel the query filters fall back to, so a row wearing it would be
        // visible to every tenant-less scope. The constraint makes that row unwritable.
        builder.ToTable("LessonSeries", t => t.HasCheckConstraint(
            "CK_LessonSeries_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0"));
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TutorTelegramId).IsRequired();
        // Ownership / scope key — every list query filters by it.
        builder.HasIndex(s => s.TutorTelegramId);

        builder.Property(s => s.StudentId).IsRequired();
        builder.HasIndex(s => s.StudentId);

        builder.Property(s => s.Title).HasMaxLength(Lesson.MaxTopicLength);

        // The recurrence rule is a value object stored inline (columns on this table).
        builder.ComplexProperty(s => s.Pattern, pattern =>
        {
            pattern.Property(p => p.Days).HasColumnName("Weekdays").IsRequired();
            pattern.Property(p => p.StartTimeLocal).HasColumnName("StartTimeLocal").IsRequired();
            pattern.Property(p => p.DurationMinutes).HasColumnName("DurationMinutes").IsRequired();
            pattern.Property(p => p.TimeZone).HasColumnName("TimeZoneId").IsRequired().HasTimeZoneConversion();
        });

        builder.Property(s => s.StartDate).IsRequired();
        builder.Property(s => s.EndDate);

        builder.Property(s => s.Price).HasPrecision(18, 2);
        builder.Property(s => s.CreatedAtUtc);
    }
}
