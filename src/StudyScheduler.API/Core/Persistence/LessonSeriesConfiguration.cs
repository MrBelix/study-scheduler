using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class LessonSeriesConfiguration : IEntityTypeConfiguration<LessonSeries>
{
    public void Configure(EntityTypeBuilder<LessonSeries> builder)
    {
        builder.ToTable("LessonSeries");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TutorTelegramId).IsRequired();
        // Materialization and overlap checks always load the tutor's active series.
        builder.HasIndex(s => new { s.TutorTelegramId, s.IsActive });

        builder.Property(s => s.Title).HasMaxLength(200);
        builder.Property(s => s.TimeZone).IsRequired().HasColumnName("TimeZoneId").HasTimeZoneConversion();

        // Flags combos round-trip as "Monday, Thursday" via the enum-to-string converter.
        builder.Property(s => s.Weekdays)
            .HasConversion<string>()
            .HasMaxLength(100);

        builder.Property(s => s.Price).HasPrecision(18, 2);
        builder.Property(s => s.CreatedAtUtc);

        builder.HasOne<Student>()
            .WithMany()
            .HasForeignKey(s => s.StudentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
