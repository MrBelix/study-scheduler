using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Localized notification copy (uk/en). Ukrainian is the default fall-through. The follow-up
/// callback payload is <c>{action}:{lessonId:N}</c> — one action letter, a colon and the 32-char
/// guid — well under Telegram's 64-byte callback-data limit.
/// </summary>
public sealed class NotificationText
{
    public string Reminder(AppLanguage lang, string studentName, DateTimeOffset localStart) => lang switch
    {
        AppLanguage.En => $"🔔 Reminder: lesson with {studentName} at {localStart:HH:mm}",
        _ => $"🔔 Нагадування: урок з {studentName} о {localStart:HH:mm}",
    };

    public string FollowUp(AppLanguage lang, string studentName) => lang switch
    {
        AppLanguage.En => $"📝 How did the lesson with {studentName} go?",
        _ => $"📝 Як пройшов урок з {studentName}?",
    };

    public IReadOnlyList<NotificationButton> ReminderButtons(AppLanguage lang, Guid lessonId) => lang switch
    {
        // Shares the follow-up's 'x' cancel: both obey the single Completed-status guard in the handler
        // (a Scheduled lesson cancels regardless of time; a Completed one is protected).
        AppLanguage.En => [new NotificationButton("❌ Cancel", $"x:{lessonId:N}")],
        _ => [new NotificationButton("❌ Скасувати", $"x:{lessonId:N}")],
    };

    /// <summary>Toast shown when a Cancel is tapped on a lesson already recorded as Completed.</summary>
    public string CancelAlreadyCompleted(AppLanguage lang) => lang switch
    {
        AppLanguage.En => "The lesson is already marked completed",
        _ => "Урок уже відмічено проведеним",
    };

    /// <summary>
    /// The localized marker appended to a notification's text after a button tap succeeds, recording
    /// the outcome once the inline keyboard is stripped.
    /// </summary>
    public string ResultMarker(AppLanguage lang, LessonStatus status, bool paid) => (status, paid, lang) switch
    {
        (LessonStatus.Cancelled, _, AppLanguage.En) => "❌ Cancelled",
        (LessonStatus.Cancelled, _, _) => "❌ Скасовано",
        (LessonStatus.Completed, true, AppLanguage.En) => "✅ Completed · 💰 Paid",
        (LessonStatus.Completed, true, _) => "✅ Проведено · 💰 Оплачено",
        (LessonStatus.Completed, false, AppLanguage.En) => "✅ Completed",
        _ => "✅ Проведено",
    };

    public IReadOnlyList<NotificationButton> FollowUpButtons(AppLanguage lang, Guid lessonId) => lang switch
    {
        AppLanguage.En =>
        [
            new NotificationButton("✅ Done", $"c:{lessonId:N}"),
            new NotificationButton("💰 Completed + paid", $"p:{lessonId:N}"),
            new NotificationButton("❌ Cancelled", $"x:{lessonId:N}"),
        ],
        _ =>
        [
            new NotificationButton("✅ Проведено", $"c:{lessonId:N}"),
            new NotificationButton("💰 Проведено + оплачено", $"p:{lessonId:N}"),
            new NotificationButton("❌ Скасовано", $"x:{lessonId:N}"),
        ],
    };
}
