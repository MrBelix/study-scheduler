using System.Globalization;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// A toast shown after a callback-driven action, answered on the callback query. Text sourced from
/// spec §7 — the one place the bot addresses the tutor as "ви" (an error asks for an action rather
/// than reporting one).
/// </summary>
public enum NotificationToast
{
    LessonMarked,
    MarkedAsPaid,
    LessonCancelled,
    AllMarked,
    AlreadyChanged,
    NoLongerExists,
    CouldNotSave,
}

/// <summary>
/// Every literal notification string, uk + en, in one place — sourced verbatim from
/// <c>docs/notifications-design-spec-2026-08-05.md</c>. Ukrainian is the fall-through. No
/// notification wording lives anywhere else in the codebase.
/// </summary>
internal static class NotificationCopy
{
    private static readonly string[] UkWeeklyAdverb =
        ["щонеділі", "щопонеділка", "щовівторка", "щосереди", "щочетверга", "щоп'ятниці", "щосуботи"];

    // ---- Words ----

    public static string LessonWord(AppLanguage lang, int n) =>
        NotificationFormatting.Plural(lang, n, "урок", "уроки", "уроків", "lesson", "lessons");

    // ---- N1 · reminder, before start ----

    public static string ReminderHeader(AppLanguage lang, int minutes, string firstName, string start) => lang switch
    {
        AppLanguage.En => $"🔔 In {minutes} min — <b>{firstName}</b>, <b>{start}</b>",
        _ => $"🔔 Через {minutes} хв — <b>{firstName}</b>, <b>{start}</b>",
    };

    public static string ReminderTimeRange(AppLanguage lang, string start, string end, int duration) => lang switch
    {
        AppLanguage.En => $"{start}–{end} · {duration} min",
        _ => $"{start}–{end} · {duration} хв",
    };

    public static string OpenButton(AppLanguage lang) => lang == AppLanguage.En ? "📱 Open" : "📱 Відкрити";

    public static string RescheduleButton(AppLanguage lang) => lang == AppLanguage.En ? "⏰ Reschedule" : "⏰ Перенести";

    public static string CancelLessonButton(AppLanguage lang) =>
        lang == AppLanguage.En ? "❌ Cancel lesson" : "❌ Скасувати урок";

    // ---- N2 · moved ----

    public static string MovedHeader(AppLanguage lang, string firstName, string newStart) => lang switch
    {
        AppLanguage.En => $"⏰ Moved — <b>{firstName}</b>, now <b>{newStart}</b>",
        _ => $"⏰ Перенесено — <b>{firstName}</b>, тепер <b>{newStart}</b>",
    };

    public static string MovedWasLine(AppLanguage lang, string dayWord, string oldStart) => lang switch
    {
        AppLanguage.En => $"was {dayWord} {oldStart}",
        _ => $"було {dayWord} {oldStart}",
    };

    public static string OpenLessonButton(AppLanguage lang) =>
        lang == AppLanguage.En ? "📱 Open lesson" : "📱 Відкрити урок";

    // ---- N3 · cancelled / completed / started / removed ----

    public static string CancelledHeader(AppLanguage lang, string firstName, string start) => lang switch
    {
        AppLanguage.En => $"❌ Cancelled — <b>{firstName}</b>, <b>{start}</b>",
        _ => $"❌ Скасовано — <b>{firstName}</b>, <b>{start}</b>",
    };

    public static string PaymentStaysLine(AppLanguage lang, string price) => lang switch
    {
        AppLanguage.En => $"payment stays · {price}",
        _ => $"оплата лишається · {price}",
    };

    public static string CompletedHeader(AppLanguage lang, string firstName, string start) => lang switch
    {
        AppLanguage.En => $"✅ Done — <b>{firstName}</b>, <b>{start}</b>",
        _ => $"✅ Проведено — <b>{firstName}</b>, <b>{start}</b>",
    };

    public static string NotPaidLine(AppLanguage lang, string price) => lang switch
    {
        AppLanguage.En => $"💰 not paid · {price}",
        _ => $"💰 не оплачено · {price}",
    };

    public static string StartedHeader(AppLanguage lang, string firstName, string start) => lang switch
    {
        AppLanguage.En => $"🔔 Started — <b>{firstName}</b>, <b>{start}</b>",
        _ => $"🔔 Почався — <b>{firstName}</b>, <b>{start}</b>",
    };

    public static string StartedTopicLine(AppLanguage lang, string topic, string end) => lang switch
    {
        AppLanguage.En => $"{topic} · until {end}",
        _ => $"{topic} · до {end}",
    };

    public static string RemovedHeader(AppLanguage lang, string firstName, string start) => lang switch
    {
        AppLanguage.En => $"🔔 Lesson deleted — <b>{firstName}</b>, <b>{start}</b>",
        _ => $"🔔 Урок видалено — <b>{firstName}</b>, <b>{start}</b>",
    };

    public static string DoneButton(AppLanguage lang) => lang == AppLanguage.En ? "✅ Done" : "✅ Проведено";

    public static string DonePaidButton(AppLanguage lang) =>
        lang == AppLanguage.En ? "💰 Done + paid" : "💰 Ще й оплачено";

    // ---- A1/A2/A3 · morning agenda ----

    public static string AgendaHeaderMulti(
        AppLanguage lang, int count, string lessonWord, string first, string last) => lang switch
    {
        AppLanguage.En => $"☀️ Today — <b>{count} {lessonWord}</b>, {first}–{last}",
        _ => $"☀️ Сьогодні <b>{count} {lessonWord}</b> — {first}–{last}",
    };

    public static string AgendaHeaderOne(AppLanguage lang, string firstName, string start) => lang switch
    {
        AppLanguage.En => $"☀️ Today <b>one lesson</b> — {firstName}, <b>{start}</b>",
        _ => $"☀️ Сьогодні <b>один урок</b> — {firstName}, <b>{start}</b>",
    };

    public static string AgendaHeaderFree(AppLanguage lang, int n, string lessonWord) => lang switch
    {
        AppLanguage.En => $"☀️ Today <b>free</b> — all {n} {lessonWord} cancelled",
        _ => $"☀️ Сьогодні <b>вільно</b> — усі {n} {lessonWord} скасовано",
    };

    public static string OpenScheduleButton(AppLanguage lang) =>
        lang == AppLanguage.En ? "📅 Open schedule" : "📅 Відкрити розклад";

    public static string DebtSuffix(AppLanguage lang, string amount) =>
        lang == AppLanguage.En ? $"💰 debt {amount}" : $"💰 борг {amount}";

    public static string UpdatedLine(AppLanguage lang, string time, string delta) => lang switch
    {
        AppLanguage.En => $"<i>updated {time} · {delta}</i>",
        _ => $"<i>оновлено {time} · {delta}</i>",
    };

    public static string DeltaAdded(AppLanguage lang, int n) =>
        lang == AppLanguage.En ? $"+{n} {LessonWord(lang, n)}" : $"+{n} {LessonWord(lang, n)}";

    public static string DeltaCancelled(AppLanguage lang, int n) =>
        lang == AppLanguage.En ? $"{n} cancelled" : $"{n} скасовано";

    public static string DeltaMoved(AppLanguage lang, int n) =>
        lang == AppLanguage.En ? $"{n} moved" : $"{n} перенесено";

    /// <summary>"щовівторка" / "every Tuesday" — the localized weekly-series description (spec 🔁).</summary>
    public static string WeeklyRule(AppLanguage lang, Weekdays days)
    {
        var selected = Enum.GetValues<DayOfWeek>().Where(d => days.Contains(d)).ToList();
        if (selected.Count == 0)
            return string.Empty;

        if (lang == AppLanguage.En)
        {
            return selected.Count == 1
                ? $"every {CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(selected[0])}"
                : "weekly: " + string.Join(
                    ", ", selected.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)));
        }

        return selected.Count == 1
            ? UkWeeklyAdverb[(int)selected[0]]
            : "щотижня: " + string.Join(", ", selected.Select(d => UkWeeklyAdverb[(int)d]));
    }

    // ---- S1 · evening summary, list ----

    public static string SummaryListHeader(AppLanguage lang, int n, string lessonWord) => lang switch
    {
        AppLanguage.En => $"🌙 <b>{n} {lessonWord}</b> left to mark",
        _ => $"🌙 Лишилось відмітити <b>{n} {lessonWord}</b>",
    };

    public static string PaidSuffix(AppLanguage lang) => lang == AppLanguage.En ? " · 💰 paid" : " · 💰 оплачено";

    public static string AllDoneButton(AppLanguage lang) => lang == AppLanguage.En ? "✅ Mark all done" : "✅ Усі проведено";

    public static string MarkRestButton(AppLanguage lang) =>
        lang == AppLanguage.En ? "✅ Mark the rest" : "✅ Решту проведено";

    public static string MoreLessonsButton(AppLanguage lang, int n) => lang == AppLanguage.En
        ? $"More {n} {LessonWord(lang, n)}"
        : $"Ще {n} {LessonWord(lang, n)}";

    // ---- S2 · evening summary, focus step ----

    public static string SummaryFocusHeader(AppLanguage lang, string time, string studentName) => lang switch
    {
        AppLanguage.En => $"🌙 <b>{time} · {studentName}</b> — how did it go?",
        _ => $"🌙 <b>{time} · {studentName}</b> — як пройшов?",
    };

    public static string FreeLabel(AppLanguage lang) => lang == AppLanguage.En ? "free" : "безкоштовно";

    public static string NotHappenedButton(AppLanguage lang) =>
        lang == AppLanguage.En ? "❌ Didn't happen" : "❌ Не було";

    public static string BackButton(AppLanguage lang) => lang == AppLanguage.En ? "← Back" : "← Назад";

    // ---- S3 · evening summary, day closed ----

    public static string DayClosedHeader(AppLanguage lang, string earned) => lang switch
    {
        AppLanguage.En => $"🌙 Day closed — <b>{earned}</b>",
        _ => $"🌙 День закрито — <b>{earned}</b>",
    };

    public static string DoneAndPaidLine(AppLanguage lang, int done, string paid) => lang switch
    {
        AppLanguage.En => $"✅ {done} done · 💰 {paid} paid",
        _ => $"✅ {done} проведено · 💰 {paid} оплачено",
    };

    public static string NotHappenedLine(AppLanguage lang, int cancelled) => lang switch
    {
        AppLanguage.En => $"❌ {cancelled} didn't happen",
        _ => $"❌ {cancelled} не було",
    };

    public static string DebtForDayLine(AppLanguage lang, string debt) => lang switch
    {
        AppLanguage.En => $"💰 debt for the day — {debt}",
        _ => $"💰 борг за день — {debt}",
    };

    public static string DayClosedAllMarkedHeader(AppLanguage lang, int n, string lessonWord) => lang switch
    {
        AppLanguage.En => $"🌙 Day closed — all {n} {lessonWord} marked",
        _ => $"🌙 День закрито — усі {n} {lessonWord} відмічено",
    };

    public static string AllPaidLine(AppLanguage lang, string sum) => lang switch
    {
        AppLanguage.En => $"{sum} · all paid",
        _ => $"{sum} · усе оплачено",
    };

    public static string TomorrowButton(AppLanguage lang, int n, string lessonWord) => lang == AppLanguage.En
        ? $"📅 Tomorrow — {n} {lessonWord}"
        : $"📅 Завтра — {n} {lessonWord}";

    // ---- §7 toasts ----

    public static string Toast(AppLanguage lang, NotificationToast toast, int count) => (toast, lang) switch
    {
        (NotificationToast.LessonMarked, AppLanguage.En) => "Lesson marked",
        (NotificationToast.LessonMarked, _) => "Урок відмічено",
        (NotificationToast.MarkedAsPaid, AppLanguage.En) => "Marked as paid",
        (NotificationToast.MarkedAsPaid, _) => "Проведено й оплачено",
        (NotificationToast.LessonCancelled, AppLanguage.En) => "Lesson cancelled",
        (NotificationToast.LessonCancelled, _) => "Урок скасовано",
        (NotificationToast.AllMarked, AppLanguage.En) => $"Done — {count} marked",
        (NotificationToast.AllMarked, _) => $"Готово — {count} {LessonWord(lang, count)} відмічено",
        (NotificationToast.AlreadyChanged, AppLanguage.En) => "Already changed",
        (NotificationToast.AlreadyChanged, _) => "Урок уже змінили в застосунку",
        (NotificationToast.NoLongerExists, AppLanguage.En) => "Lesson no longer exists",
        (NotificationToast.NoLongerExists, _) => "Цього уроку більше немає",
        (NotificationToast.CouldNotSave, AppLanguage.En) => "Couldn't save — try again",
        (NotificationToast.CouldNotSave, _) => "Не вдалось зберегти — спробуйте ще",
        _ => throw new ArgumentOutOfRangeException(nameof(toast), toast, null),
    };
}
