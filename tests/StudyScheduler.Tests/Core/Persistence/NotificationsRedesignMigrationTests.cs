using Microsoft.EntityFrameworkCore.Migrations.Operations;
using StudyScheduler.API.Core.Persistence.Migrations;
using Xunit;

namespace StudyScheduler.Tests.Core.Persistence;

/// <summary>
/// The notifications redesign moves the per-lesson "already sent" flags onto dispatch rows. The
/// ORDER is the whole risk: the seed reads <c>ReminderSentAtUtc</c>, so it must run while the column
/// still exists — a scaffolder that reorders the operations would silently drop every live reminder
/// on the floor and re-send them all.
/// </summary>
public class NotificationsRedesignMigrationTests
{
    private static SqlOperation Seed(NotificationsRedesign migration) =>
        Assert.Single(migration.UpOperations.OfType<SqlOperation>());

    /// <summary>The seed SQL with runs of whitespace collapsed, so assertions ignore its layout.</summary>
    private static string SeedSql(NotificationsRedesign migration) =>
        string.Join(' ', Seed(migration).Sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void UpOperations_NotificationsRedesign_SeedTheDispatchesBeforeDroppingTheLessonFlags()
    {
        // Arrange
        var sut = new NotificationsRedesign();

        // Act
        var operations = sut.UpOperations.ToList();

        // Assert
        // The seed reads the two columns the drops below remove, so it has to come first.
        var seedIndex = operations.IndexOf(Seed(sut));
        var dropIndexes = operations
            .Select((operation, index) => (operation, index))
            .Where(x => x.operation is DropColumnOperation { Table: "Lessons" })
            .Select(x => x.index)
            .ToList();

        Assert.Equal(2, dropIndexes.Count);
        Assert.All(dropIndexes, dropIndex => Assert.True(seedIndex < dropIndex));
    }

    [Fact]
    public void UpOperations_NotificationsRedesign_SeedOnlyStillLiveReminders()
    {
        // Arrange
        var sut = new NotificationsRedesign();

        // Act
        var sql = SeedSql(sut);

        // Assert
        // A reminder that never went out owes nothing, and one whose lesson already ended owes nothing
        // either — only the intersection is still live and must carry over.
        Assert.Contains("INSERT INTO \"NotificationDispatches\"", sql);
        Assert.Contains("\"ReminderSentAtUtc\" IS NOT NULL", sql);
        Assert.Contains("\"EndUtc\" > NOW()", sql);
    }

    [Fact]
    public void UpOperations_NotificationsRedesign_SeedWritesDeliveredRowsWithNoMessageId()
    {
        // Arrange
        var sut = new NotificationsRedesign();

        // Act
        var sql = SeedSql(sut);

        // Assert
        // Delivered, so the row de-duplicates the next tick; MessageId NULL, because those messages
        // went out before we recorded their ids and nothing can edit them. The chat is the tutor's own
        // private chat with the bot, which is the very chat id the runner passes.
        Assert.Contains("'Reminder'", sql);
        Assert.Contains("'Delivered'", sql);
        Assert.Contains("l.\"TutorTelegramId\", NULL, '{}'::jsonb", sql);
    }

    [Fact]
    public void UpOperations_NotificationsRedesign_CreateTheDispatchTableWithBothPartialUniques()
    {
        // Arrange
        var sut = new NotificationsRedesign();

        // Act
        var indexes = sut.UpOperations
            .OfType<CreateIndexOperation>()
            .Where(o => o.Table == "NotificationDispatches" && o.IsUnique)
            .ToList();

        // Assert
        // Both uniques are filtered on State='Delivered': a Retracted row deliberately does NOT block
        // a new dispatch, which is exactly what re-arms a reminder after a reschedule.
        Assert.Equal(2, indexes.Count);
        Assert.All(indexes, index => Assert.Contains("\"State\" = 'Delivered'", index.Filter));
    }

    [Fact]
    public void DownOperations_NotificationsRedesign_ReverseTheSchemaButNotTheSeed()
    {
        // Arrange
        var sut = new NotificationsRedesign();

        // Act
        var operations = sut.DownOperations;

        // Assert
        // Every schema change comes back; the seed does not, because the flags it read are gone.
        Assert.Contains(operations, o => o is DropTableOperation { Name: "NotificationDispatches" });
        Assert.Contains(operations, o => o is AddColumnOperation { Table: "Lessons", Name: "ReminderSentAtUtc" });
        Assert.Contains(operations, o => o is RenameColumnOperation { Table: "TutorProfiles", NewName: "NotifyAfterLesson" });
        Assert.Empty(operations.OfType<SqlOperation>());
    }
}
