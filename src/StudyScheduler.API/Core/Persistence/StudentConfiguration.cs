using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> builder)
    {
        builder.ToTable("Students");
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
