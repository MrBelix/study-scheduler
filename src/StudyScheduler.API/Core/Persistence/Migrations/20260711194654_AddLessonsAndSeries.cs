using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonsAndSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Lessons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TutorTelegramId = table.Column<long>(type: "bigint", nullable: false),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurrenceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsPaid = table.Column<bool>(type: "bit", nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lessons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LessonSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TutorTelegramId = table.Column<long>(type: "bigint", nullable: false),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Weekdays = table.Column<int>(type: "int", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    StartTimeLocal = table.Column<TimeOnly>(type: "time", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonSeries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_SeriesId_OccurrenceDate",
                table: "Lessons",
                columns: new[] { "SeriesId", "OccurrenceDate" },
                unique: true,
                filter: "[SeriesId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_TutorTelegramId_StartUtc",
                table: "Lessons",
                columns: new[] { "TutorTelegramId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LessonSeries_StudentId",
                table: "LessonSeries",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonSeries_TutorTelegramId",
                table: "LessonSeries",
                column: "TutorTelegramId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Lessons");

            migrationBuilder.DropTable(
                name: "LessonSeries");
        }
    }
}
