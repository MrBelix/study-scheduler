using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LessonSeriesSlotIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Lessons_SeriesSlotPair",
                table: "Lessons",
                sql: "(\"SeriesId\" IS NULL) = (\"OccurrenceDate\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Lessons_LessonSeries_SeriesId",
                table: "Lessons",
                column: "SeriesId",
                principalTable: "LessonSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Lessons_LessonSeries_SeriesId",
                table: "Lessons");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lessons_SeriesSlotPair",
                table: "Lessons");
        }
    }
}
