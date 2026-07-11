using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class LessonSeriesConfiguration : IEntityTypeConfiguration<LessonSeries>
{
    public void Configure(EntityTypeBuilder<LessonSeries> builder)
    {
        builder.ToTable("LessonSeries");
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
