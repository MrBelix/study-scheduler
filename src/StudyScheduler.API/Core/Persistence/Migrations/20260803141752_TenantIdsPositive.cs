using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantIdsPositive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_TutorProfiles_TelegramUserIdPositive",
                table: "TutorProfiles",
                sql: "\"TelegramUserId\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Students_TutorTelegramIdPositive",
                table: "Students",
                sql: "\"TutorTelegramId\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LessonSeries_TutorTelegramIdPositive",
                table: "LessonSeries",
                sql: "\"TutorTelegramId\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lessons_TutorTelegramIdPositive",
                table: "Lessons",
                sql: "\"TutorTelegramId\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TutorProfiles_TelegramUserIdPositive",
                table: "TutorProfiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Students_TutorTelegramIdPositive",
                table: "Students");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LessonSeries_TutorTelegramIdPositive",
                table: "LessonSeries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lessons_TutorTelegramIdPositive",
                table: "Lessons");
        }
    }
}
