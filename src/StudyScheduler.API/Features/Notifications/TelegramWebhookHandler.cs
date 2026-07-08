using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Applies follow-up button presses coming through the Telegram webhook: resolves the slot
/// (materializing a virtual series occurrence on demand, same as the occurrence PATCH endpoint),
/// applies the action, confirms via <c>answerCallbackQuery</c> and rewrites the prompt message
/// with the outcome. Everything is best-effort towards Telegram: the lesson mutation is the
/// source of truth, a failed edit/answer is only logged.
/// </summary>
public sealed class TelegramWebhookHandler(
    ILessonRepository lessons,
    ILessonSeriesRepository series,
    IStudentRepository students,
    ITutorProfileRepository profiles,
    IUnitOfWork uow,
    LessonMaterializer materializer,
    ITelegramBotClient bot,
    ILogger<TelegramWebhookHandler> logger)
{
    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        // Only callback_query updates are subscribed; anything else is acknowledged silently.
        if (update.CallbackQuery is not { } callback)
            return;

        if (!FollowUpCallback.TryParse(callback.Data, out var action, out var slotRef))
        {
            logger.LogWarning("Ignoring unparsable callback data from user {UserId}", callback.From.Id);
            return;
        }

        var tutorId = callback.From.Id;
        var profile = await profiles.GetAsync(tutorId, ct);
        var lang = profile?.LanguageCode;

        var (lesson, isNew) = await ResolveLessonAsync(tutorId, slotRef, ct);
        if (lesson is null)
        {
            await bot.AnswerCallbackQueryAsync(callback.Id, NotificationMessages.CallbackNotFound(lang), ct);
            return;
        }

        Apply(lesson, action);
        if (isNew)
            lessons.Add(lesson);
        else
            lessons.Update(lesson);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (isNew && SqlErrors.IsDuplicateKey(exception))
        {
            // Double-tap race: another press materialized the slot first — duplicate-key on the
            // unique (SeriesId, OccurrenceDate) index only; anything else bubbles up to the
            // webhook endpoint's catch-all. The failed insert must be discarded or every later
            // save in this scope retries the doomed row.
            logger.LogWarning(
                exception,
                "Concurrent materialization for slot {SeriesId}/{OccurrenceDate}; retrying as update",
                lesson.SeriesId, lesson.OccurrenceDate);
            uow.DiscardChanges();
            lesson = await lessons.GetBySeriesOccurrenceAsync(lesson.SeriesId!.Value, lesson.OccurrenceDate!.Value, ct);
            if (lesson is null)
            {
                // Still answer the callback so the user's button spinner doesn't hang forever.
                await bot.AnswerCallbackQueryAsync(callback.Id, NotificationMessages.CallbackNotFound(lang), ct);
                return;
            }
            Apply(lesson, action);
            lessons.Update(lesson);
            await uow.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Applied follow-up {Action} to lesson {LessonId} for tutor {TutorTelegramId}",
            action, lesson.Id, tutorId);

        await ConfirmAsync(callback, action, lesson, profile, ct);
    }

    /// <summary><c>IsNew</c> — the lesson was just materialized and has no row yet.</summary>
    private async Task<(Lesson? Lesson, bool IsNew)> ResolveLessonAsync(
        long tutorId, LessonSlotRef slotRef, CancellationToken ct)
    {
        if (slotRef.LessonId is { } lessonId)
        {
            var lesson = await lessons.GetByIdAsync(lessonId, tutorId, track: true, ct);
            return (lesson, false);
        }

        // Not tutor-scoped at the query level, so ownership must still be checked here.
        var existing = await lessons.GetBySeriesOccurrenceAsync(slotRef.SeriesId!.Value, slotRef.OccurrenceDate!.Value, ct);
        if (existing is not null)
            return (existing.TutorTelegramId == tutorId ? existing : null, false);

        var owner = await series.GetByIdAsync(slotRef.SeriesId.Value, tutorId, ct: ct);
        if (owner is null)
            return (null, false);

        var slot = owner.GetOccurrences(slotRef.OccurrenceDate.Value, slotRef.OccurrenceDate.Value);
        return slot.Count == 0
            ? (null, false)
            : (await materializer.MaterializeSlotAsync(owner, slot[0], ct), true);
    }

    private void Apply(Lesson lesson, FollowUpAction action)
    {
        var result = action switch
        {
            FollowUpAction.Completed => lesson.ChangeStatus(LessonStatus.Completed),
            FollowUpAction.Paid => lesson.ChangeStatus(LessonStatus.Completed),
            FollowUpAction.Cancelled => lesson.ChangeStatus(LessonStatus.Cancelled),
            _ => Result.Success(),
        };

        if (!result.IsSuccess)
        {
            // The statuses above are fixed, defined enum values, so a failure can't come from
            // legitimate input — log and skip the mutation (defensive, no throw: the webhook
            // must still answer the callback so the user's button spinner doesn't hang).
            logger.LogWarning(
                "Skipping follow-up {Action} on lesson {LessonId}: {Errors}",
                action, lesson.Id, string.Join("; ", result.Errors.Select(e => e.Message)));
            return;
        }

        if (action == FollowUpAction.Paid)
            lesson.SetPaid(true);
    }

    private async Task ConfirmAsync(
        TelegramCallbackQuery callback,
        FollowUpAction action,
        Lesson lesson,
        TutorProfile? profile,
        CancellationToken ct)
    {
        var lang = profile?.LanguageCode;
        await bot.AnswerCallbackQueryAsync(callback.Id, NotificationMessages.CallbackDone(lang), ct);

        if (callback.Message is not { } message)
            return;

        var student = await students.GetByIdAsync(lesson.StudentId, lesson.TutorTelegramId, ct: ct);
        var timeRange = LessonNotifier.FormatTimeRange(
            ScheduleSlot.From(lesson), profile?.TimeZone ?? TimeZoneInfo.Utc);
        await bot.EditMessageTextAsync(
            message.Chat.Id,
            message.MessageId,
            NotificationMessages.FollowUpResult(lang, action, student?.Name ?? "?", timeRange),
            ct);
    }
}
