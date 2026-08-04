using Microsoft.EntityFrameworkCore.Migrations.Operations;
using StudyScheduler.API.Core.Persistence.Migrations;
using Xunit;

namespace StudyScheduler.Tests.Core.Persistence;

/// <summary>
/// The backfill half of the archive cascade: students archived before the cascade existed still own a
/// running schedule, and this migration applies the same two statements to them. It must stay
/// DATA-ONLY — no column, index or constraint is touched — and both statements must reach rows
/// through the student they belong to, which is what keeps them inside one tutor's data.
/// </summary>
public class ArchivedStudentsScheduleCascadeMigrationTests
{
    [Fact]
    public void UpOperations_ArchivedStudentsScheduleCascade_AreTwoDataStatementsAndNothingElse()
    {
        // Arrange
        var sut = new ArchivedStudentsScheduleCascade();

        // Act
        var operations = sut.UpOperations;

        // Assert
        // Raw SQL only: applying this over a live database cannot rewrite the schema.
        Assert.All(operations, operation => Assert.IsType<SqlOperation>(operation));
        Assert.Equal(2, operations.Count);
    }

    [Fact]
    public void UpOperations_ArchivedStudentsScheduleCascade_EndTheSeriesOfArchivedStudentsOnly()
    {
        // Arrange
        var sut = new ArchivedStudentsScheduleCascade();

        // Act
        var sql = ((SqlOperation)sut.UpOperations[0]).Sql;

        // Assert
        // The end date only ever moves earlier (the domain's End() rule), and the rows are reached
        // through the archived student they belong to — no tenant filter is bypassed.
        Assert.Contains("UPDATE \"LessonSeries\"", sql);
        Assert.Contains("FROM \"Students\"", sql);
        Assert.Contains("\"Status\" = 'Archived'", sql);
        Assert.Contains("\"EndDate\" IS NULL", sql);
    }

    [Fact]
    public void UpOperations_ArchivedStudentsScheduleCascade_DeleteFutureLessonsExceptCompletedOnes()
    {
        // Arrange
        var sut = new ArchivedStudentsScheduleCascade();

        // Act
        var sql = ((SqlOperation)sut.UpOperations[1]).Sql;

        // Assert
        // Same reach through the student row, the same two exemptions the runtime cascade makes:
        // a lesson that already started, and a completed one wherever it sits.
        Assert.Contains("DELETE FROM \"Lessons\"", sql);
        Assert.Contains("USING \"Students\"", sql);
        Assert.Contains("\"Status\" = 'Archived'", sql);
        Assert.Contains("\"StartUtc\" > NOW()", sql);
        Assert.Contains("\"Status\" <> 'Completed'", sql);
    }

    [Fact]
    public void DownOperations_ArchivedStudentsScheduleCascade_DoNothing()
    {
        // Arrange
        var sut = new ArchivedStudentsScheduleCascade();

        // Act
        var operations = sut.DownOperations;

        // Assert — deleted lessons and overwritten end dates are unrecoverable; there is no undo.
        Assert.Empty(operations);
    }
}
