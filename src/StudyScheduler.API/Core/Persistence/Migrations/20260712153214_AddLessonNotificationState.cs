using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonNotificationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FollowUpSentAtUtc",
                table: "Lessons",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReminderSentAtUtc",
                table: "Lessons",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FollowUpSentAtUtc",
                table: "Lessons");

            migrationBuilder.DropColumn(
                name: "ReminderSentAtUtc",
                table: "Lessons");
        }
    }
}
