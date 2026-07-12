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

    public IReadOnlyList<NotificationButton> FollowUpButtons(AppLanguage lang, Guid lessonId) => lang switch
    {
        AppLanguage.En =>
        [
            new NotificationButton("✅ Done", $"c:{lessonId:N}"),
            new NotificationButton("💰 Paid", $"p:{lessonId:N}"),
            new NotificationButton("❌ Cancelled", $"x:{lessonId:N}"),
        ],
        _ =>
        [
            new NotificationButton("✅ Проведено", $"c:{lessonId:N}"),
            new NotificationButton("💰 Оплачено", $"p:{lessonId:N}"),
            new NotificationButton("❌ Скасовано", $"x:{lessonId:N}"),
        ],
    };
}
