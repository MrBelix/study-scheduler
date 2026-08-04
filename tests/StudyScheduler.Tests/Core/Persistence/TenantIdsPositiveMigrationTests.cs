using Microsoft.EntityFrameworkCore.Migrations.Operations;
using StudyScheduler.API.Core.Persistence.Migrations;
using Xunit;

namespace StudyScheduler.Tests.Core.Persistence;

/// <summary>
/// The migration that puts the "a tenant id is positive" invariant into the schema itself. It must be
/// exactly four CHECK constraints and nothing else: no data touched, no column rewritten — which is
/// also why it applies over an existing database without a backfill (every stored owner came from a
/// Telegram id, and those are positive).
/// </summary>
public class TenantIdsPositiveMigrationTests
{
    private static readonly (string Table, string Name, string Sql)[] Expected =
    [
        ("TutorProfiles", "CK_TutorProfiles_TelegramUserIdPositive", "\"TelegramUserId\" > 0"),
        ("Students", "CK_Students_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0"),
        ("LessonSeries", "CK_LessonSeries_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0"),
        ("Lessons", "CK_Lessons_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0"),
    ];

    [Fact]
    public void UpOperations_TenantIdsPositive_AddsTheFourPositivityChecksAndNothingElse()
    {
        // Arrange
        var sut = new TenantIdsPositive();

        // Act
        var operations = sut.UpOperations;

        // Assert
        // Every tutor-owned table, plus the profile whose KEY is the tenancy key — and nothing else,
        // so applying this over live data cannot rewrite a row.
        Assert.All(operations, operation => Assert.IsType<AddCheckConstraintOperation>(operation));
        Assert.Equal(
            Expected,
            operations.Cast<AddCheckConstraintOperation>().Select(c => (c.Table, c.Name, c.Sql)));
    }

    [Fact]
    public void DownOperations_TenantIdsPositive_DropsExactlyThoseChecks()
    {
        // Arrange
        var sut = new TenantIdsPositive();

        // Act
        var operations = sut.DownOperations;

        // Assert
        Assert.All(operations, operation => Assert.IsType<DropCheckConstraintOperation>(operation));
        Assert.Equal(
            Expected.Select(e => (e.Table, e.Name)),
            operations.Cast<DropCheckConstraintOperation>().Select(d => (d.Table, d.Name)));
    }
}
