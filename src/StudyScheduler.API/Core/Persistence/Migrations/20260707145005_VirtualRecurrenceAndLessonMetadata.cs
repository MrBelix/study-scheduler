using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VirtualRecurrenceAndLessonMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Virtual-recurrence cleanup: future series slots that were never touched (still
            // scheduled, no topic, not paid; Description does not exist yet, so it is trivially
            // empty) are pure materialization pollution — from now on they are expanded on the
            // fly, so the physical rows must go. One-off lessons (SeriesId IS NULL), past rows
            // and anything modified (cancelled, completed, topic'd, paid) are kept.
            migrationBuilder.Sql(
                """
                DELETE FROM [Lessons]
                WHERE [SeriesId] IS NOT NULL
                  AND [Status] = N'Scheduled'
                  AND [Topic] IS NULL
                  AND [IsPaid] = 0
                  AND [StartUtc] > SYSUTCDATETIME();
                """);

            // Shrinking Topic to 200 chars: clip any longer legacy values first so the
            // ALTER COLUMN cannot fail.
            migrationBuilder.Sql(
                """
                UPDATE [Lessons]
                SET [Topic] = LEFT([Topic], 200)
                WHERE LEN([Topic]) > 200;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Topic",
                table: "Lessons",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Lessons",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "Lessons");

            migrationBuilder.AlterColumn<string>(
                name: "Topic",
                table: "Lessons",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
