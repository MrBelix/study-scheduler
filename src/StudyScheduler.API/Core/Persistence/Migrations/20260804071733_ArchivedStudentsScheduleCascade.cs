using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArchivedStudentsScheduleCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only, and the backfill half of the archive cascade (LessonService.StopTeachingAsync):
            // from now on, archiving a student ends their running series and deletes their future
            // lessons, which is what lets the overlap checker ignore student status altogether. Students
            // archived BEFORE that existed still own a running schedule, so the same two statements are
            // applied to them here — otherwise their ghost lessons would keep blocking new bookings and
            // their series would keep generating rows every night.
            //
            // Tenancy: both statements join the student row itself, and a lesson or series always
            // belongs to the same tutor as its student. Ownership is therefore inherent — no filter is
            // bypassed and no row of another tutor can be reached through an archived student of this
            // one.
            //
            // Time zones: the runtime cascade ends a series on "yesterday" in the series' OWN zone. SQL
            // has no such notion here (the zone is a column of the series, not of this statement), so it
            // approximates with the earliest local date anywhere on Earth — UTC now minus 12 hours —
            // and steps one day back from it. That is at worst ONE DAY TIGHTER than the runtime path and
            // never looser, which is the safe direction: a slightly earlier end date can only stop the
            // generator from producing occurrences it must not produce anyway, whereas a later one would
            // let the nightly pass re-create a lesson the DELETE below just removed. Like the domain's
            // End(), the WHERE clause only ever tightens: a series that already stops earlier is left
            // exactly as it is.
            migrationBuilder.Sql("""
                UPDATE "LessonSeries" AS s
                SET "EndDate" = ((NOW() AT TIME ZONE 'UTC') - INTERVAL '12 hours')::date - 1
                FROM "Students" AS st
                WHERE st."Id" = s."StudentId"
                  AND st."Status" = 'Archived'
                  AND (s."EndDate" IS NULL
                       OR s."EndDate" > ((NOW() AT TIME ZONE 'UTC') - INTERVAL '12 hours')::date - 1);
                """);

            // The rows themselves: everything of an archived student that has not started yet goes,
            // one-offs and generated occurrences alike, hand-edited ones included — exactly what the
            // runtime cascade sweeps. A COMPLETED lesson is spared wherever it sits (it happened and
            // carries the payment the debt dashboard counts), and so is a lesson already under way:
            // the cut is by instant, not by date. Status is persisted as a string, hence the text
            // comparison.
            migrationBuilder.Sql("""
                DELETE FROM "Lessons" AS l
                USING "Students" AS st
                WHERE st."Id" = l."StudentId"
                  AND st."Status" = 'Archived'
                  AND l."StartUtc" > NOW()
                  AND l."Status" <> 'Completed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the deleted lessons and the end dates the series carried before are
            // unrecoverable, and re-opening those series would put a schedule back on students the
            // tutor no longer teaches.
        }
    }
}
