using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillCustomizedSeriesLessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only, and the other half of the migration that added "IsCustomized" with a false
            // default. Series lessons used to be written ONLY when somebody mutated an occurrence
            // (materialize-on-mutation): every pre-existing slot row is therefore a hand-made decision
            // — a reschedule, a price, a topic, a single-occurrence cancel — and the flag has to say
            // so, or the first schedule edit or series cancel would sweep those rows away and
            // regenerate them as plain scheduled lessons.
            // Runs before any lesson is generated: migrations are applied at startup (see
            // PersistenceExtensions.ApplyMigrations), ahead of the nightly generator's first tick, so
            // nothing this marks can be a generated row.
            migrationBuilder.Sql("""
                UPDATE "Lessons" SET "IsCustomized" = TRUE WHERE "SeriesId" IS NOT NULL AND NOT "IsCustomized";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: which rows were customized before the backfill is unrecoverable,
            // and clearing the flag wholesale would throw away the genuine ones set since.
        }
    }
}
