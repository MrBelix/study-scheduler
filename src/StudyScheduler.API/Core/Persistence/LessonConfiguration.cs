using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        // A lesson is either a one-off (neither column set) or one slot of a series (both set).
        // Lesson.Create already refuses the half-filled pair; the check constraint makes it a database
        // invariant, so no code path — migration, script or manual fix — can leave an orphan half.
        // The second constraint is tenancy's: 0 is the "no tenant" sentinel the query filters fall
        // back to, so a row wearing it would be visible to every tenant-less scope. A Telegram id is
        // positive by construction; the database now refuses anything else.
        builder.ToTable("Lessons", t =>
        {
            t.HasCheckConstraint(
                "CK_Lessons_SeriesSlotPair", "(\"SeriesId\" IS NULL) = (\"OccurrenceDate\" IS NULL)");
            t.HasCheckConstraint("CK_Lessons_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0");
        });
        builder.HasKey(l => l.Id);

        builder.Property(l => l.TutorTelegramId).IsRequired();
        // Range reads ("my schedule between X and Y") filter by tutor then by start.
        builder.HasIndex(l => new { l.TutorTelegramId, l.StartUtc });

        builder.Property(l => l.StudentId).IsRequired();

        builder.Property(l => l.SeriesId);
        builder.Property(l => l.OccurrenceDate);

        // One row per series slot — the constraint generation is idempotent against. Partial index:
        // one-off lessons (SeriesId null) are kept out of it entirely, so it only guards real slots.
        builder.HasIndex(l => new { l.SeriesId, l.OccurrenceDate })
            .IsUnique()
            .HasFilter("\"SeriesId\" IS NOT NULL");

        // A slot-bound lesson must point at a real series. No navigation property: Lesson is its own
        // aggregate root and only keeps the id. Restrict, not Cascade — series are never hard-deleted
        // (cancelling only tightens EndDate), so a delete attempt is a bug and must fail loudly rather
        // than silently take the generated lessons with it.
        builder.HasOne<LessonSeries>()
            .WithMany()
            .HasForeignKey(l => l.SeriesId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(l => l.StartUtc).IsRequired();
        builder.Property(l => l.EndUtc).IsRequired();
        builder.Property(l => l.DurationMinutes).IsRequired();

        builder.Property(l => l.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(l => l.Price).HasPrecision(18, 2);
        builder.Property(l => l.IsPaid).IsRequired();

        // Every pre-existing row is a row someone touched on purpose (nothing else used to be
        // written) — the BackfillCustomizedSeriesLessons migration flags them as such — so the
        // column's false default is only ever the starting point of a GENERATED row.
        builder.Property(l => l.IsCustomized).IsRequired();

        builder.Property(l => l.Topic).HasMaxLength(Lesson.MaxTopicLength);
        builder.Property(l => l.Description).HasMaxLength(Lesson.MaxDescriptionLength);

        builder.Property(l => l.CreatedAtUtc);
    }
}
