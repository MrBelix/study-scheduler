using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeCancelledLessonsUnpaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only: enforce the new invariant "a cancelled lesson can never be paid" on rows
            // written before it existed. Status is persisted as a string, hence the text comparison.
            migrationBuilder.Sql("""
                UPDATE "Lessons" SET "IsPaid" = false WHERE "Status" = 'Cancelled' AND "IsPaid";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the pre-normalization paid flags are unrecoverable.
        }
    }
}
