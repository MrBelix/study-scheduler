using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class LessonNotificationConfiguration : IEntityTypeConfiguration<LessonNotification>
{
    public void Configure(EntityTypeBuilder<LessonNotification> builder)
    {
        builder.ToTable("LessonNotifications");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();

        builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(20);
        // "S:{guid}:{yyyy-MM-dd}" is the longest form (49 chars).
        builder.Property(n => n.SlotKey).IsRequired().HasMaxLength(60);
        builder.Property(n => n.SentAtUtc);

        // The dedup contract: one notification of a kind per slot per tutor.
        builder.HasIndex(n => new { n.TutorTelegramId, n.Kind, n.SlotKey }).IsUnique();
    }
}
