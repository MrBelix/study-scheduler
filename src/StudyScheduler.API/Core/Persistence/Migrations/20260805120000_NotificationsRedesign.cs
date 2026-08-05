using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyScheduler.API.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationsRedesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The dispatch table: from now on a bot message is a ROW, not a flag on the lesson it
            // talks about. No foreign key to "Lessons" on purpose — a reminder must survive its lesson
            // being deleted, because "🔔 Урок видалено" is exactly what the tutor still has to be told.
            // The two unique indexes are PARTIAL: a Retracted row deliberately does NOT block a new
            // dispatch for the same target, which is what re-arms a reminder after a reschedule.
            migrationBuilder.CreateTable(
                name: "NotificationDispatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TutorTelegramId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: true),
                    RenderedSnapshot = table.Column<string>(type: "jsonb", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDispatches", x => x.Id);
                    table.CheckConstraint("CK_NotificationDispatches_Target", "((\"Kind\" = 'Reminder') = (\"LessonId\" IS NOT NULL)) AND ((\"Kind\" <> 'Reminder') = (\"LocalDate\" IS NOT NULL))");
                    table.CheckConstraint("CK_NotificationDispatches_TutorTelegramIdPositive", "\"TutorTelegramId\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatches_ChatId_MessageId",
                table: "NotificationDispatches",
                columns: new[] { "ChatId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatches_State_ExpiresAtUtc",
                table: "NotificationDispatches",
                columns: new[] { "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatches_TutorTelegramId_Kind_LessonId",
                table: "NotificationDispatches",
                columns: new[] { "TutorTelegramId", "Kind", "LessonId" },
                unique: true,
                filter: "\"State\" = 'Delivered' AND \"LessonId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatches_TutorTelegramId_Kind_LocalDate",
                table: "NotificationDispatches",
                columns: new[] { "TutorTelegramId", "Kind", "LocalDate" },
                unique: true,
                filter: "\"State\" = 'Delivered' AND \"LocalDate\" IS NOT NULL");

            // 2. Carry the reminders that are STILL LIVE over to the new table, BEFORE the flags they
            // are read from are dropped below. Only a lesson whose reminder went out and that has not
            // ended yet still owes anything, so the seed filters on both. ChatId = TutorTelegramId
            // because the bot messages the tutor's own private chat, which is the very chat id the
            // runner passes today. MessageId stays NULL: those messages went out before we recorded
            // their ids, so nothing can edit them — reconciliation skips the edit and just retires the
            // row. FollowUpSentAtUtc is deliberately NOT seeded: the per-lesson follow-up no longer
            // exists, so there is nothing left to de-duplicate.
            migrationBuilder.Sql("""
                INSERT INTO "NotificationDispatches"
                  ("Id","TutorTelegramId","Kind","LessonId","LocalDate","ChatId","MessageId","RenderedSnapshot","ContentHash","State","ExpiresAtUtc","SentAtUtc","LastSyncedAtUtc")
                SELECT gen_random_uuid(), l."TutorTelegramId", 'Reminder', l."Id", NULL, l."TutorTelegramId", NULL,
                       '{}'::jsonb, '', 'Delivered', l."EndUtc", l."ReminderSentAtUtc", l."ReminderSentAtUtc"
                FROM "Lessons" l
                WHERE l."ReminderSentAtUtc" IS NOT NULL AND l."EndUtc" > NOW();
                """);

            // 3. The per-lesson dedup flags are the dispatch rows above from now on.
            migrationBuilder.DropColumn(
                name: "FollowUpSentAtUtc",
                table: "Lessons");

            migrationBuilder.DropColumn(
                name: "ReminderSentAtUtc",
                table: "Lessons");

            // 4. The after-lesson follow-up became the evening day summary — same opt-in, new meaning,
            // so the column is RENAMED rather than dropped and re-added (every tutor's existing choice
            // survives). The morning agenda is a NEW opt-in: off by default, at 08:00 local once on.
            migrationBuilder.RenameColumn(
                name: "NotifyAfterLesson",
                table: "TutorProfiles",
                newName: "DaySummary");

            migrationBuilder.AddColumn<bool>(
                name: "MorningAgenda",
                table: "TutorProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "MorningAgendaAtLocal",
                table: "TutorProfiles",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(8, 0, 0));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverses 4 → 3 → 1. The SEED (2) has no undo, exactly like
            // BackfillCustomizedSeriesLessons.Down: the flags it read are dropped by this very
            // migration, so there is nothing left to write them back from. Rolling back therefore
            // re-arms every still-live reminder, which is the harmless direction — one duplicate
            // message at worst, never a missed one.
            migrationBuilder.DropColumn(
                name: "MorningAgenda",
                table: "TutorProfiles");

            migrationBuilder.DropColumn(
                name: "MorningAgendaAtLocal",
                table: "TutorProfiles");

            migrationBuilder.RenameColumn(
                name: "DaySummary",
                table: "TutorProfiles",
                newName: "NotifyAfterLesson");

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpSentAtUtc",
                table: "Lessons",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAtUtc",
                table: "Lessons",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.DropTable(
                name: "NotificationDispatches");
        }
    }
}
