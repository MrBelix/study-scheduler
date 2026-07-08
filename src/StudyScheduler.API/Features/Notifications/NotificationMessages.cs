namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Bot message texts, localized by the tutor's profile language ("en" → English, anything else —
/// Ukrainian, the product default). Times are pre-formatted by the caller in the tutor's zone.
/// </summary>
public static class NotificationMessages
{
    public static string Reminder(string? lang, string studentName, string timeRange, int minutesUntil) =>
        IsEnglish(lang)
            ? $"🔔 Lesson with {studentName} in {minutesUntil} min ({timeRange})"
            : $"🔔 Через {minutesUntil} хв урок з {studentName} ({timeRange})";

    public static string FollowUpPrompt(string? lang, string studentName, string timeRange) =>
        IsEnglish(lang)
            ? $"Lesson with {studentName} ({timeRange}) has ended. What happened?"
            : $"Урок з {studentName} ({timeRange}) завершився. Що з ним?";

    public static IReadOnlyList<BotButton> FollowUpButtons(
        string? lang, Guid? lessonId, Guid? seriesId, DateOnly? occurrenceDate)
    {
        var english = IsEnglish(lang);
        return
        [
            new BotButton(
                english ? "✅ Completed" : "✅ Проведено",
                FollowUpCallback.Format(FollowUpAction.Completed, lessonId, seriesId, occurrenceDate)),
            new BotButton(
                english ? "💰 Paid" : "💰 Оплачено",
                FollowUpCallback.Format(FollowUpAction.Paid, lessonId, seriesId, occurrenceDate)),
            new BotButton(
                english ? "❌ Cancelled" : "❌ Скасовано",
                FollowUpCallback.Format(FollowUpAction.Cancelled, lessonId, seriesId, occurrenceDate)),
        ];
    }

    /// <summary>Replaces the follow-up prompt after a button was pressed.</summary>
    public static string FollowUpResult(string? lang, FollowUpAction action, string studentName, string timeRange)
    {
        var english = IsEnglish(lang);
        var label = action switch
        {
            FollowUpAction.Completed => english ? "✅ Completed" : "✅ Проведено",
            FollowUpAction.Paid => english ? "💰 Paid" : "💰 Оплачено",
            _ => english ? "❌ Cancelled" : "❌ Скасовано",
        };

        return $"{label} — {studentName}, {timeRange}";
    }

    public static string CallbackDone(string? lang) => IsEnglish(lang) ? "Done ✅" : "Готово ✅";

    public static string CallbackNotFound(string? lang) =>
        IsEnglish(lang) ? "Lesson not found" : "Урок не знайдено";

    private static bool IsEnglish(string? lang) => lang == "en";
}
