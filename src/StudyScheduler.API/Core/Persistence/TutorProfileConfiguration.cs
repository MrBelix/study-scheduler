using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class TutorProfileConfiguration : IEntityTypeConfiguration<TutorProfile>
{
    public void Configure(EntityTypeBuilder<TutorProfile> builder)
    {
        builder.ToTable("TutorProfiles");

        // Natural key: one profile per Telegram user, id comes from Telegram.
        builder.HasKey(p => p.TelegramUserId);
        builder.Property(p => p.TelegramUserId).ValueGeneratedNever();

        builder.Property(p => p.TimeZone).IsRequired().HasColumnName("TimeZoneId").HasTimeZoneConversion();
        builder.Property(p => p.LanguageCode).HasMaxLength(2);
        builder.Property(p => p.RemindMinutes);
        builder.Property(p => p.NotifyAfterLesson);
        builder.Property(p => p.CreatedAtUtc);
    }
}
