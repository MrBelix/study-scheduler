using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Processes a single Telegram <see cref="Telegram.Bot.Types.Update"/>: re-enables a tutor's bot
/// reachability on any interaction (a <c>/start</c> or a button tap resumes notifications we'd
/// disabled after a 403), and turns a follow-up inline button into a lesson mutation. The handler is
/// deliberately resilient — a malformed update is answered (or ignored) but never throws, so the
/// endpoint can always ack 200 and Telegram won't retry-storm.
/// </summary>
public sealed class TelegramWebhookHandler(
    ILessonRepository lessons,
    LessonPatchService patchService,
    ITutorProfileRepository profiles,
    IUnitOfWork uow,
    INotificationSender sender,
    NotificationText text,
    ILogger<TelegramWebhookHandler> logger)
{
    public async Task HandleAsync(Telegram.Bot.Types.Update update, CancellationToken ct = default)
    {
        // Any interaction from a tutor whose bot we'd flagged unreachable resumes their notifications.
        var userId = update.CallbackQuery?.From.Id ?? update.Message?.From?.Id;
        if (userId is { } id)
            await EnsureReachableAsync(id, ct);

        if (update.CallbackQuery is { } cq)
        {
            await HandleCallbackAsync(cq, ct);
            return;
        }

        // Non-callback, non-message updates (edits, channel posts, …) carry no action for us.
    }

    /// <summary>Flips a previously-disabled bot back on so the poller starts targeting the tutor again.</summary>
    private async Task EnsureReachableAsync(long userId, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(userId, ct);
        if (profile is null || profile.BotReachable)
            return;

        profile.MarkBotReachable();
        profiles.Update(profile);
        await uow.SaveChangesAsync(ct);
        logger.LogInformation("Re-enabled bot reachability for tutor {TutorId} after an interaction", userId);
    }

    private async Task HandleCallbackAsync(Telegram.Bot.Types.CallbackQuery cq, CancellationToken ct)
    {
        if (!TryParse(cq.Data, out var request, out var lessonId))
        {
            logger.LogWarning("Malformed callback data '{Data}' from tutor {TutorId}", cq.Data, cq.From.Id);
            await sender.AnswerCallbackAsync(cq.Id, "?", ct);
            return;
        }

        // Scoping the load to the caller enforces ownership: another tutor's lesson reads as missing.
        var lesson = await lessons.GetByIdAsync(lessonId, cq.From.Id, track: true, ct);
        if (lesson is null)
        {
            logger.LogWarning(
                "Callback for lesson {LessonId} not found for tutor {TutorId}", lessonId, cq.From.Id);
            await sender.AnswerCallbackAsync(cq.Id, "Not found", ct);
            return;
        }

        // A Cancel (shared by the reminder button and the follow-up's ❌) may drop a Scheduled lesson
        // at any time — a late/no-show student is a legitimate cancel even after the scheduled start,
        // and we only ever know the scheduled start, not the actual one. But a lesson the tutor has
        // already recorded as Completed must not be silently undone: refuse and toast instead.
        if (request.Status == LessonStatus.Cancelled && lesson.Status == LessonStatus.Completed)
        {
            var lang = await ResolveLanguageAsync(cq.From.Id, ct);
            logger.LogInformation(
                "Cancel for lesson {LessonId} ignored: already recorded Completed (tutor {TutorId})",
                lessonId, cq.From.Id);
            await sender.AnswerCallbackAsync(cq.Id, text.CancelAlreadyCompleted(lang), ct);
            return;
        }

        var outcome = await patchService.ApplyAsync(lesson, request, cq.From.Id, isNew: false, ct: ct);
        if (outcome is LessonPatchOutcome.Ok)
        {
            logger.LogInformation(
                "Applied callback mutation to lesson {LessonId} for tutor {TutorId}", lessonId, cq.From.Id);

            // Turn the tapped notification into a lasting record: append a localized result marker to
            // its text and strip the keyboard so it can't be re-tapped. An inaccessible message (rare)
            // just keeps the toast.
            if (cq.Message is { } message)
            {
                var lang = await ResolveLanguageAsync(cq.From.Id, ct);
                var marker = text.ResultMarker(lang, request.Status!.Value, request.IsPaid == true);
                var originalText = message.Text ?? string.Empty;
                await sender.EditMessageAsync(message.Chat.Id, message.MessageId, $"{originalText}\n\n{marker}", ct);
            }

            await sender.AnswerCallbackAsync(cq.Id, "✓", ct);
        }
        else
        {
            logger.LogWarning(
                "Callback mutation of lesson {LessonId} for tutor {TutorId} failed with {Outcome}",
                lessonId, cq.From.Id, outcome.GetType().Name);
            await sender.AnswerCallbackAsync(cq.Id, "Could not update", ct);
        }
    }

    /// <summary>Resolves the tutor's chosen language for a toast, defaulting to Ukrainian.</summary>
    private async Task<AppLanguage> ResolveLanguageAsync(long tutorId, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(tutorId, ct);
        return profile?.LanguageCode ?? AppLanguage.Uk;
    }

    /// <summary>
    /// Parses a payload <c>"{action}:{lessonId:N}"</c> into the lesson id and the update it maps to.
    /// <c>c</c> → Completed, <c>p</c> → Completed + paid, <c>x</c> → Cancelled. The reminder and the
    /// follow-up both use <c>x</c>; the handler applies the same Completed-status cancel guard to it.
    /// </summary>
    private static bool TryParse(string? data, out UpdateLessonRequest request, out Guid lessonId)
    {
        request = default!;
        lessonId = default;

        if (data is null)
            return false;

        var colon = data.IndexOf(':');
        if (colon != 1)
            return false;

        if (!Guid.TryParseExact(data[(colon + 1)..], "N", out lessonId))
            return false;

        request = data[0] switch
        {
            'c' => new UpdateLessonRequest(null, null, LessonStatus.Completed, null, null, null, null),
            'p' => new UpdateLessonRequest(null, null, LessonStatus.Completed, null, true, null, null),
            'x' => new UpdateLessonRequest(null, null, LessonStatus.Cancelled, null, null, null, null),
            _ => null!,
        };
        return request is not null;
    }
}
