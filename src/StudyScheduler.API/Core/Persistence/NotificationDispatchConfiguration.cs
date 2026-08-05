using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StudyScheduler.Domain.Notifications;

namespace StudyScheduler.API.Core.Persistence;

internal sealed class NotificationDispatchConfiguration : IEntityTypeConfiguration<NotificationDispatch>
{
    // WhenWritingNull keeps the stored document to the half that is actually in play (a reminder row
    // carries no Day, a digest row no Reminder) instead of two nulls per row forever.
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public void Configure(EntityTypeBuilder<NotificationDispatch> builder)
    {
        // The first constraint is tenancy's: 0 is the "no tenant" sentinel the query filters fall
        // back to, so a row wearing it would be visible to every tenant-less scope. The second is the
        // aggregate's own shape — a reminder targets a lesson, a digest targets a local date, and
        // neither can target both or nothing.
        builder.ToTable("NotificationDispatches", t =>
        {
            t.HasCheckConstraint("CK_NotificationDispatches_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0");
            t.HasCheckConstraint(
                "CK_NotificationDispatches_Target",
                "((\"Kind\" = 'Reminder') = (\"LessonId\" IS NOT NULL)) AND ((\"Kind\" <> 'Reminder') = (\"LocalDate\" IS NOT NULL))");
        });
        builder.HasKey(d => d.Id);

        builder.Property(d => d.TutorTelegramId).IsRequired();

        builder.Property(d => d.Kind)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(d => d.LessonId);
        builder.Property(d => d.LocalDate);

        // NO foreign key to Lessons on purpose: a reminder must survive its lesson being deleted,
        // because "🔔 Урок видалено" is exactly what the tutor still has to be told.

        builder.Property(d => d.ChatId).IsRequired();
        builder.Property(d => d.MessageId);

        // The snapshot travels as one jsonb document. The comparer is not optional: records hold
        // collections, so without comparing the serialized form EF cannot see an in-place snapshot
        // change on a tracked row and would silently skip the UPDATE.
        builder.Property(d => d.Snapshot)
            .HasColumnName("RenderedSnapshot")
            .HasColumnType("jsonb")
            .HasConversion(
                new ValueConverter<RenderedSnapshot, string>(
                    v => JsonSerializer.Serialize(v, SnapshotJson),
                    s => JsonSerializer.Deserialize<RenderedSnapshot>(s, SnapshotJson)!),
                new ValueComparer<RenderedSnapshot>(
                    (l, r) => JsonSerializer.Serialize(l, SnapshotJson) == JsonSerializer.Serialize(r, SnapshotJson),
                    v => JsonSerializer.Serialize(v, SnapshotJson).GetHashCode(),
                    v => JsonSerializer.Deserialize<RenderedSnapshot>(
                        JsonSerializer.Serialize(v, SnapshotJson), SnapshotJson)!));

        builder.Property(d => d.ContentHash).IsRequired().HasMaxLength(64);

        builder.Property(d => d.State)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(d => d.ExpiresAtUtc).IsRequired();
        builder.Property(d => d.SentAtUtc).IsRequired();
        builder.Property(d => d.LastSyncedAtUtc).IsRequired();

        // One live reminder per lesson, one live digest per (kind, date). The filters are what makes
        // them work: a Retracted row deliberately does NOT block a new dispatch — that is exactly
        // what re-arms a reminder after the lesson is rescheduled.
        builder.HasIndex(d => new { d.TutorTelegramId, d.Kind, d.LessonId })
            .IsUnique()
            .HasFilter("\"State\" = 'Delivered' AND \"LessonId\" IS NOT NULL");
        builder.HasIndex(d => new { d.TutorTelegramId, d.Kind, d.LocalDate })
            .IsUnique()
            .HasFilter("\"State\" = 'Delivered' AND \"LocalDate\" IS NOT NULL");

        // The reconciliation queue reads live rows and retires the expired ones.
        builder.HasIndex(d => new { d.State, d.ExpiresAtUtc });
        // The webhook resolves a tapped message back to the dispatch it belongs to.
        builder.HasIndex(d => new { d.ChatId, d.MessageId });
    }
}
