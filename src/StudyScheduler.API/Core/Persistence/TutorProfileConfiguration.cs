using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

        // Store the enum as its lowercase two-letter code — the column stays nvarchar(2), so
        // existing "uk"/"en" rows keep working with no data migration. EF applies this
        // non-nullable converter to the nullable property; DB NULL maps to a null language.
        builder.Property(p => p.LanguageCode)
            .HasConversion(new ValueConverter<AppLanguage, string>(
                v => v.ToCode(),
                s => AppLanguageCode.ParseCode(s).Value))
            .HasMaxLength(2);
        builder.Property(p => p.RemindMinutes);
        builder.Property(p => p.NotifyAfterLesson);
        // Optimistic reachability: existing rows backfill to reachable via the DB default.
        builder.Property(p => p.BotReachable).HasDefaultValue(true);
        builder.Property(p => p.CreatedAtUtc);
    }
}
