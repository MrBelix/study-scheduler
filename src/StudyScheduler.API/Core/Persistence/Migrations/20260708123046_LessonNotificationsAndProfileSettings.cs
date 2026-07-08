using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LessonNotificationsAndProfileSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Notifications are on by default — existing profiles get the same defaults a
            // freshly created one would (TutorProfile: 30 min reminder, follow-up enabled).
            migrationBuilder.AddColumn<bool>(
                name: "NotifyAfterLesson",
                table: "TutorProfiles",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "RemindMinutes",
                table: "TutorProfiles",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("UPDATE TutorProfiles SET RemindMinutes = 30;");

            migrationBuilder.CreateTable(
                name: "LessonNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TutorTelegramId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SlotKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonNotifications_TutorTelegramId_Kind_SlotKey",
                table: "LessonNotifications",
                columns: new[] { "TutorTelegramId", "Kind", "SlotKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonNotifications");

            migrationBuilder.DropColumn(
                name: "NotifyAfterLesson",
                table: "TutorProfiles");

            migrationBuilder.DropColumn(
                name: "RemindMinutes",
                table: "TutorProfiles");
        }
    }
}
