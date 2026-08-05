using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationRendererReminderTests
{
    private static readonly Guid LessonId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Start = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(60);

    private static NotificationRenderer Build(string miniAppUrl = "https://app.example.com") =>
        new(Options.Create(new NotificationsOptions { MiniAppUrl = miniAppUrl }));

    private static ReminderView View(
        ReminderPhase phase = ReminderPhase.BeforeStart,
        int remindMinutes = 30,
        string studentName = "Іван Петренко",
        string? topic = "Present Perfect",
        decimal price = 600m,
        bool isPaid = false,
        DateTimeOffset? originalStart = null,
        bool wasPaidBeforeChange = false,
        DateTimeOffset? nowLocal = null) =>
        new(
            AppLanguage.Uk, LessonId, phase, remindMinutes, studentName, topic,
            Start, End, 60, price, isPaid, originalStart, wasPaidBeforeChange, nowLocal ?? Start);

    [Fact]
    public void Reminder_BeforeStart_RendersHeaderStudentTopicAndTimeRange()
    {
        // Arrange
        var view = View();

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal(
            "🔔 Через 30 хв — <b>Іван</b>, <b>14:00</b>\n" +
            "Іван Петренко\n" +
            "Present Perfect\n" +
            "14:00–15:00 · 60 хв",
            message.Text);
    }

    [Fact]
    public void Reminder_BeforeStartEmptyTopic_OmitsTopicLineEntirely()
    {
        // Arrange
        var view = View(topic: null);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.DoesNotContain("Present Perfect", message.Text);
        Assert.Equal(3, message.Text.Split('\n').Length);
    }

    [Fact]
    public void Reminder_BeforeStart_NeverMentionsMoney()
    {
        // Arrange
        var view = View(price: 600m, isPaid: false);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.DoesNotContain("₴", message.Text);
    }

    [Fact]
    public void Reminder_BeforeStart_UsesFrozenRemindMinutesNotLiveCountdown()
    {
        // Arrange — the value carried by the view (frozen at first send), not derived from the clock.
        var view = View(remindMinutes: 45);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Contains("Через 45 хв", message.Text);
    }

    [Fact]
    public void Reminder_BeforeStartWithMiniAppUrl_EmitsOpenRescheduleAndCancelRowsInOrder()
    {
        // Arrange
        var view = View();

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal(3, message.Rows.Count);
        var open = Assert.Single(message.Rows[0].Buttons);
        Assert.Equal($"https://app.example.com?startapp=lesson-{LessonId:N}", open.Url);
        Assert.Null(open.CallbackData);

        var reschedule = Assert.Single(message.Rows[1].Buttons);
        Assert.Equal($"https://app.example.com?startapp=lesson-{LessonId:N}-reschedule", reschedule.Url);

        var cancel = Assert.Single(message.Rows[2].Buttons);
        Assert.Equal($"x:{LessonId:N}", cancel.CallbackData);
        Assert.Null(cancel.Url);
    }

    [Fact]
    public void Reminder_BeforeStartNoMiniAppUrl_OmitsUrlRowsButKeepsCancel()
    {
        // Arrange
        var view = View();

        // Act
        var message = Build(miniAppUrl: "").Reminder(view);

        // Assert
        var row = Assert.Single(message.Rows);
        Assert.Equal($"x:{LessonId:N}", Assert.Single(row.Buttons).CallbackData);
    }

    [Fact]
    public void Reminder_Moved_RendersOldStartFromOriginalSnapshotAndOnlyOpenButton()
    {
        // Arrange — was moved from 09:00 the same day to 14:00.
        var originalStart = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        var view = View(phase: ReminderPhase.Moved, originalStart: originalStart);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal(
            "⏰ Перенесено — <b>Іван</b>, тепер <b>14:00</b>\n" +
            "Іван Петренко\n" +
            "було сьогодні 09:00",
            message.Text);
        var button = Assert.Single(Assert.Single(message.Rows).Buttons);
        Assert.Equal("📱 Відкрити урок", button.Text);
        Assert.NotNull(button.Url);
        Assert.Null(button.CallbackData);
    }

    [Fact]
    public void Reminder_Moved_CrossDayReschedule_UsesActualNowAsDayWordReference()
    {
        // Arrange — the reminder was for today 15:00; the tutor moves it to tomorrow and reads the
        // notice today, so "було" must still say "сьогодні", not the new start's own (tomorrow's) date.
        var now = new DateTimeOffset(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);
        var originalStart = new DateTimeOffset(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);
        var newStart = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
        var view = new ReminderView(
            AppLanguage.Uk, LessonId, ReminderPhase.Moved, 30, "Іван Петренко", "Present Perfect",
            newStart, newStart.AddMinutes(60), 60, 600m, false, originalStart, false, now);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Contains("було сьогодні 15:00", message.Text);
    }

    [Fact]
    public void Reminder_CancelledFreeLesson_OmitsPaymentStaysLine()
    {
        // Arrange — a free lesson is always "paid" by domain default (price 0 => IsPaid true), so the
        // payment-stays line must not fire off WasPaidBeforeChange alone.
        var view = View(phase: ReminderPhase.Cancelled, price: 0m, wasPaidBeforeChange: true);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal("❌ Скасовано — <b>Іван</b>, <b>14:00</b>", message.Text);
    }

    [Fact]
    public void Reminder_Cancelled_WasPaidBeforeChange_ShowsPaymentStaysLine()
    {
        // Arrange — the domain already cleared IsPaid on cancel; the snapshot remembers it was paid.
        var view = View(phase: ReminderPhase.Cancelled, price: 600m, wasPaidBeforeChange: true);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal(
            "❌ Скасовано — <b>Іван</b>, <b>14:00</b>\n" +
            "оплата лишається · 600 ₴",
            message.Text);
        Assert.Empty(message.Rows);
    }

    [Fact]
    public void Reminder_CancelledWasNotPaid_OmitsPaymentLine()
    {
        // Arrange
        var view = View(phase: ReminderPhase.Cancelled, wasPaidBeforeChange: false);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal("❌ Скасовано — <b>Іван</b>, <b>14:00</b>", message.Text);
    }

    [Fact]
    public void Reminder_CompletedUnpaidWithPrice_ShowsNotPaidLine()
    {
        // Arrange
        var view = View(phase: ReminderPhase.Completed, price: 600m, isPaid: false);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal(
            "✅ Проведено — <b>Іван</b>, <b>14:00</b>\n" +
            "💰 не оплачено · 600 ₴",
            message.Text);
    }

    [Fact]
    public void Reminder_CompletedFreeLesson_OmitsNotPaidLine()
    {
        // Arrange — price 0 never counts as unpaid.
        var view = View(phase: ReminderPhase.Completed, price: 0m, isPaid: true);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal("✅ Проведено — <b>Іван</b>, <b>14:00</b>", message.Text);
    }

    [Fact]
    public void Reminder_CompletedPaid_OmitsNotPaidLine()
    {
        // Arrange
        var view = View(phase: ReminderPhase.Completed, price: 600m, isPaid: true);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal("✅ Проведено — <b>Іван</b>, <b>14:00</b>", message.Text);
    }

    [Fact]
    public void Reminder_Started_ShowsTopicAndEndAndBothMarkButtons()
    {
        // Arrange
        var view = View(phase: ReminderPhase.Started, price: 600m);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal(
            "🔔 Почався — <b>Іван</b>, <b>14:00</b>\n" +
            "Present Perfect · до 15:00",
            message.Text);
        Assert.Equal(2, message.Rows.Count);
        Assert.Equal($"c:{LessonId:N}", Assert.Single(message.Rows[0].Buttons).CallbackData);
        Assert.Equal($"p:{LessonId:N}", Assert.Single(message.Rows[1].Buttons).CallbackData);
    }

    [Fact]
    public void Reminder_StartedEmptyTopic_OmitsSecondLineEntirely()
    {
        // Arrange
        var view = View(phase: ReminderPhase.Started, topic: null);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal("🔔 Почався — <b>Іван</b>, <b>14:00</b>", message.Text);
    }

    [Fact]
    public void Reminder_StartedFreeLesson_OmitsPaidButton()
    {
        // Arrange
        var view = View(phase: ReminderPhase.Started, price: 0m);

        // Act
        var message = Build().Reminder(view);

        // Assert
        var row = Assert.Single(message.Rows);
        Assert.Equal($"c:{LessonId:N}", Assert.Single(row.Buttons).CallbackData);
    }

    [Fact]
    public void Reminder_Removed_ShowsDeletedHeaderAndNoButtons()
    {
        // Arrange
        var view = View(phase: ReminderPhase.Removed);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Equal("🔔 Урок видалено — <b>Іван</b>, <b>14:00</b>", message.Text);
        Assert.Empty(message.Rows);
    }

    [Fact]
    public void Reminder_StudentNameWithMarkup_IsEscapedInBody()
    {
        // Arrange
        var view = View(studentName: "A & B <script>");

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.DoesNotContain("<script>", message.Text);
        Assert.Contains("&amp;", message.Text);
    }

    [Fact]
    public void Reminder_LongDoubleBarrelledName_TruncatesFirstNameInHeaderButKeepsFullNameLineBelow()
    {
        // Arrange
        var view = View(studentName: "Олександра Вишневецька-Богуславська");

        // Act
        var message = Build().Reminder(view);

        // Assert
        var lines = message.Text.Split('\n');
        Assert.Contains("<b>Олександра</b>", lines[0]);
        Assert.Equal("Олександра Вишневецька-Богуславська", lines[1]);
    }

    [Fact]
    public void Reminder_LessonAcrossMidnight_RendersStartAndEndCorrectly()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 8, 5, 23, 30, 0, TimeSpan.Zero);
        var end = start.AddMinutes(60);
        var view = new ReminderView(
            AppLanguage.Uk, LessonId, ReminderPhase.BeforeStart, 30, "Іван Петренко", null,
            start, end, 60, 0m, true, null, false, start);

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Contains("23:30–00:30", message.Text);
    }

    [Fact]
    public void Reminder_EnglishLocale_RendersEnglishHeaderAndTimeRange()
    {
        // Arrange
        var view = View() with { Lang = AppLanguage.En };

        // Act
        var message = Build().Reminder(view);

        // Assert
        Assert.Contains("🔔 In 30 min", message.Text);
        Assert.Contains("14:00–15:00 · 60 min", message.Text);
    }

    [Theory]
    [InlineData(ReminderPhase.BeforeStart)]
    [InlineData(ReminderPhase.Moved)]
    [InlineData(ReminderPhase.Cancelled)]
    [InlineData(ReminderPhase.Completed)]
    [InlineData(ReminderPhase.Started)]
    [InlineData(ReminderPhase.Removed)]
    public void Reminder_AnyPhase_EveryButtonSetsExactlyOneOfCallbackDataOrUrl(ReminderPhase phase)
    {
        // Arrange
        var view = View(phase: phase, price: 600m);

        // Act
        var message = Build().Reminder(view);

        // Assert
        foreach (var button in message.Rows.SelectMany(r => r.Buttons))
            Assert.True(
                (button.CallbackData is null) != (button.Url is null),
                $"Button '{button.Text}' must set exactly one of CallbackData/Url.");
    }

    [Fact]
    public void Reminder_SameView_ProducesSameContentHash()
    {
        // Arrange
        var a = View();
        var b = View();

        // Act
        var first = Build().Reminder(a);
        var second = Build().Reminder(b);

        // Assert — same input, same hash; the renderer is a pure function of the view.
        Assert.Equal(first.ContentHash, second.ContentHash);
    }
}
