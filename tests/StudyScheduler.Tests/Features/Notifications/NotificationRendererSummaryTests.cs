using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationRendererSummaryTests
{
    private static readonly DateOnly Date = new(2026, 8, 5); // Wednesday

    private static NotificationRenderer Build(string miniAppUrl = "https://app.example.com") =>
        new(Options.Create(new NotificationsOptions { MiniAppUrl = miniAppUrl }));

    private static DayLineView Line(
        string name,
        int hour,
        LessonStatus status = LessonStatus.Scheduled,
        string? topic = null,
        decimal price = 500m,
        bool isPaid = false,
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            new DateTimeOffset(Date.Year, Date.Month, Date.Day, hour, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(Date.Year, Date.Month, Date.Day, hour + 1, 0, 0, TimeSpan.Zero),
            name, topic, status, isPaid, price, 0m, null);

    private static SummaryView View(
        IReadOnlyList<DayLineView> lines,
        Guid? focused = null,
        bool expanded = false,
        int tomorrow = 0,
        AppLanguage lang = AppLanguage.Uk) =>
        new(lang, Date, lines, focused, expanded, tomorrow);

    [Fact]
    public void Summary_UnmarkedLessons_RendersListHeaderAndOneButtonPerLesson()
    {
        // Arrange
        var lines = new[] { Line("Іван Петренко", 9), Line("Олег Д.", 14), Line("Аня Л.", 18) };

        // Act
        var message = Build().Summary(View(lines));

        // Assert
        Assert.Equal(
            "🌙 Лишилось відмітити <b>3 уроки</b>\n" +
            "середа, 5 серпня",
            message.Text);
        Assert.Equal(4, message.Rows.Count); // 3 lessons + "all done"
        Assert.Equal("09:00 · Іван Петренко", Assert.Single(message.Rows[0].Buttons).Text);
        Assert.Equal("✅ Усі проведено", Assert.Single(message.Rows[3].Buttons).Text);
        Assert.Equal($"a:{Date:yyyy-MM-dd}", Assert.Single(message.Rows[3].Buttons).CallbackData);
    }

    [Fact]
    public void Summary_AlreadyMarkedLessons_StayInBodyAsTicksNotButtons()
    {
        // Arrange
        var lines = new[]
        {
            Line("Іван", 9, status: LessonStatus.Completed, isPaid: true),
            Line("Олег", 14, status: LessonStatus.Cancelled),
            Line("Аня", 18),
        };

        // Act
        var message = Build().Summary(View(lines));

        // Assert
        Assert.Contains("✅ 09:00 Іван · 💰 оплачено", message.Text);
        Assert.Contains("<s>❌ 14:00 Олег</s>", message.Text);
        // Only the still-unmarked lesson is a button; the "all done" label already reads "the rest".
        var lessonRows = message.Rows.Where(r => r.Buttons[0].CallbackData!.StartsWith("s:")).ToList();
        Assert.Single(lessonRows);
        Assert.Equal("✅ Решту проведено", message.Rows[^1].Buttons[0].Text);
    }

    [Fact]
    public void Summary_MoreThanSixUnmarked_ShowsFirstSixPlusExpandButton()
    {
        // Arrange
        var lines = Enumerable.Range(9, 8).Select(h => Line($"Student {h}", h)).ToArray();

        // Act
        var message = Build().Summary(View(lines));

        // Assert
        // 6 lesson buttons + "more" + "all done" = 8 rows.
        Assert.Equal(8, message.Rows.Count);
        Assert.Equal($"m:{Date:yyyy-MM-dd}", message.Rows[6].Buttons[0].CallbackData);
        Assert.Equal("Ще 2 уроки", message.Rows[6].Buttons[0].Text);
        Assert.Equal($"a:{Date:yyyy-MM-dd}", message.Rows[7].Buttons[0].CallbackData);
    }

    [Fact]
    public void Summary_ExpandedWithMoreThanSixUnmarked_ShowsAllLessonButtons()
    {
        // Arrange
        var lines = Enumerable.Range(9, 8).Select(h => Line($"Student {h}", h)).ToArray();

        // Act
        var message = Build().Summary(View(lines, expanded: true));

        // Assert — 8 lesson buttons + "all done", no "more" row.
        Assert.Equal(9, message.Rows.Count);
        Assert.DoesNotContain(message.Rows, r => r.Buttons[0].CallbackData!.StartsWith("m:"));
    }

    [Fact]
    public void Summary_TwelveLessonsAllUnmarked_ExpandedShowsAllTwelveButtons()
    {
        // Arrange
        var lines = Enumerable.Range(8, 12).Select(h => Line($"Student {h}", h)).ToArray();

        // Act
        var message = Build().Summary(View(lines, expanded: true));

        // Assert
        Assert.Equal(13, message.Rows.Count); // 12 + all done
    }

    [Fact]
    public void Summary_ButtonLabel_CutsNameToEighteenAndWholeLabelToTwentyFive()
    {
        // Arrange
        var lines = new[] { Line("Олександра Вишневецька-Богуславська", 9) };

        // Act
        var message = Build().Summary(View(lines));

        // Assert
        var label = message.Rows[0].Buttons[0].Text;
        Assert.True(new System.Globalization.StringInfo(label).LengthInTextElements <= 25);
    }

    [Fact]
    public void Summary_Focused_RendersFocusHeaderAndPriceAndTwoPairedRows()
    {
        // Arrange
        var id = Guid.NewGuid();
        var lines = new[] { Line("Іван Петренко", 9, topic: "Grammar", price: 600m, id: id) };

        // Act
        var message = Build().Summary(View(lines, focused: id));

        // Assert
        Assert.Equal(
            "🌙 <b>09:00 · Іван Петренко</b> — як пройшов?\n" +
            "Grammar · 600 ₴",
            message.Text);
        Assert.Equal(2, message.Rows.Count);
        Assert.Equal(2, message.Rows[0].Buttons.Count);
        Assert.Equal($"c:{id:N}", message.Rows[0].Buttons[0].CallbackData);
        Assert.Equal($"p:{id:N}", message.Rows[0].Buttons[1].CallbackData);
        Assert.Equal(2, message.Rows[1].Buttons.Count);
        Assert.Equal($"x:{id:N}", message.Rows[1].Buttons[0].CallbackData);
        Assert.Equal("b:", message.Rows[1].Buttons[1].CallbackData);
    }

    [Fact]
    public void Summary_FocusedFreeLesson_ShowsFreeLabelAndDropsPaidButton()
    {
        // Arrange
        var id = Guid.NewGuid();
        var lines = new[] { Line("Іван", 9, price: 0m, id: id) };

        // Act
        var message = Build().Summary(View(lines, focused: id));

        // Assert
        Assert.Contains("безкоштовно", message.Text);
        Assert.Single(message.Rows[0].Buttons);
        Assert.Equal($"c:{id:N}", message.Rows[0].Buttons[0].CallbackData);
    }

    [Fact]
    public void Summary_FocusedEmptyTopic_OmitsTopicPrefix()
    {
        // Arrange
        var id = Guid.NewGuid();
        var lines = new[] { Line("Іван", 9, topic: null, price: 500m, id: id) };

        // Act
        var message = Build().Summary(View(lines, focused: id));

        // Assert — no topic to prefix, so the second line is just the price.
        Assert.Equal(NotificationFormatting.Money(500m), message.Text.Split('\n')[1]);
    }

    [Fact]
    public void Summary_PairedButtonRows_BothLabelsAtMostSixteenTextElements()
    {
        // Arrange
        var id = Guid.NewGuid();
        var lines = new[] { Line("Іван", 9, price: 500m, id: id) };

        // Act
        var message = Build().Summary(View(lines, focused: id));

        // Assert
        foreach (var row in message.Rows.Where(r => r.Buttons.Count == 2))
            foreach (var button in row.Buttons)
                Assert.True(NotificationFormatting.Width(button.Text) <= 16, button.Text);
    }

    [Fact]
    public void Summary_NothingUnmarkedWithCancellationsAndDebt_RendersFullClosedTemplate()
    {
        // Arrange
        var lines = new[]
        {
            Line("Іван", 9, status: LessonStatus.Completed, isPaid: true, price: 500m),
            Line("Олег", 11, status: LessonStatus.Completed, isPaid: false, price: 300m),
            Line("Аня", 14, status: LessonStatus.Cancelled),
        };

        // Act
        var message = Build().Summary(View(lines));

        // Assert
        Assert.Equal(
            $"🌙 День закрито — <b>{NotificationFormatting.Money(800m)}</b>\n" +
            $"✅ 2 проведено · 💰 {NotificationFormatting.Money(500m)} оплачено\n" +
            "❌ 1 не було\n" +
            $"💰 борг за день — {NotificationFormatting.Money(300m)}",
            message.Text);
        Assert.Empty(message.Rows);
    }

    [Fact]
    public void Summary_NothingUnmarkedAllPaidNoCancellations_RendersCompactClosedTemplate()
    {
        // Arrange
        var lines = new[]
        {
            Line("Іван", 9, status: LessonStatus.Completed, isPaid: true, price: 500m),
            Line("Олег", 11, status: LessonStatus.Completed, isPaid: true, price: 300m),
        };

        // Act
        var message = Build().Summary(View(lines));

        // Assert
        Assert.Equal(
            "🌙 День закрито — усі 2 уроки відмічено\n" +
            $"{NotificationFormatting.Money(800m)} · усе оплачено",
            message.Text);
    }

    [Fact]
    public void Summary_ClosedWithTomorrowLessons_ShowsTomorrowButton()
    {
        // Arrange
        var lines = new[] { Line("Іван", 9, status: LessonStatus.Completed, isPaid: true, price: 500m) };

        // Act
        var message = Build().Summary(View(lines, tomorrow: 4));

        // Assert
        var button = Assert.Single(Assert.Single(message.Rows).Buttons);
        Assert.Equal($"https://app.example.com?startapp=schedule-{Date.AddDays(1):yyyy-MM-dd}", button.Url);
        Assert.Contains("4 уроки", button.Text);
    }

    [Fact]
    public void Summary_ClosedWithNoTomorrowLessons_OmitsTomorrowButton()
    {
        // Arrange
        var lines = new[] { Line("Іван", 9, status: LessonStatus.Completed, isPaid: true, price: 500m) };

        // Act
        var message = Build().Summary(View(lines, tomorrow: 0));

        // Assert
        Assert.Empty(message.Rows);
    }

    [Fact]
    public void Summary_AllLessonsCancelledForTheDay_NoLessonsUnmarked_RendersClosedWithNoEarnings()
    {
        // Arrange
        var lines = new[] { Line("Іван", 9, status: LessonStatus.Cancelled) };

        // Act
        var message = Build().Summary(View(lines));

        // Assert
        Assert.Equal(
            $"🌙 День закрито — <b>{NotificationFormatting.Money(0m)}</b>\n" +
            "❌ 1 не було",
            message.Text);
    }

    [Fact]
    public void Summary_EnglishLocale_RendersEnglishListHeader()
    {
        // Arrange
        var lines = new[] { Line("Ivan", 9), Line("Maria", 11), Line("Oleg", 14) };

        // Act
        var message = Build().Summary(View(lines, lang: AppLanguage.En));

        // Assert
        Assert.StartsWith("🌙 <b>3 lessons</b> left to mark", message.Text);
    }
}
