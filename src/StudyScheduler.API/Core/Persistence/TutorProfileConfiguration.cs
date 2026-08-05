using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class TutorProfileConfiguration : IEntityTypeConfiguration<TutorProfile>
{
    public void Configure(EntityTypeBuilder<TutorProfile> builder)
    {
        // The key IS the tenancy key here, and 0 is the "no tenant" sentinel the query filters fall
        // back to: a profile keyed 0 would belong to every tenant-less scope. A Telegram id is
        // positive by construction; the database now refuses anything else.
        builder.ToTable("TutorProfiles", t => t.HasCheckConstraint(
            "CK_TutorProfiles_TelegramUserIdPositive", "\"TelegramUserId\" > 0"));

        // Natural key: one profile per Telegram user, id comes from Telegram.
        builder.HasKey(p => p.TelegramUserId);
        builder.Property(p => p.TelegramUserId).ValueGeneratedNever();

        builder.Property(p => p.TimeZone).IsRequired().HasColumnName("TimeZoneId").HasTimeZoneConversion();

        // Store the enum as its lowercase two-letter code in a varchar(2) column. EF applies this
        // non-nullable converter to the nullable property; DB NULL maps to a null language.
        builder.Property(p => p.LanguageCode)
            .HasConversion(new ValueConverter<AppLanguage, string>(
                v => v.ToCode(),
                s => AppLanguageCode.ParseCode(s).Value))
            .HasMaxLength(2);
        builder.Property(p => p.RemindMinutes);
        builder.Property(p => p.DaySummary);
        // Both opt-ins are new columns on an existing table: the DB defaults are what existing rows
        // backfill to (the morning agenda is off until the tutor asks for it, at 08:00 local).
        builder.Property(p => p.MorningAgenda).HasDefaultValue(false);
        builder.Property(p => p.MorningAgendaAtLocal).HasDefaultValue(new TimeOnly(8, 0));
        // Optimistic reachability: existing rows backfill to reachable via the DB default.
        builder.Property(p => p.BotReachable).HasDefaultValue(true);
        builder.Property(p => p.CreatedAtUtc);
    }
}
