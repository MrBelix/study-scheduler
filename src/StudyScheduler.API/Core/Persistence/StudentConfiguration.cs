using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> builder)
    {
        // Ownership is positive by construction (a Telegram id is), and the database says so: 0 is
        // the "no tenant" sentinel the query filters fall back to, so a row wearing it would be
        // visible to every tenant-less scope. The constraint makes that row unwritable.
        builder.ToTable("Students", t => t.HasCheckConstraint(
            "CK_Students_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0"));
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TutorTelegramId).IsRequired();
        // Ownership / scope key — every list query filters by it.
        builder.HasIndex(s => s.TutorTelegramId);

        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);

        builder.Property(s => s.Rate).HasPrecision(18, 2);

        builder.Property(s => s.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(s => s.CreatedAtUtc);
    }
}
