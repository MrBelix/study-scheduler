using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationRendererAgendaTests
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
        decimal debt = 0m,
        string? seriesRule = null,
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            new DateTimeOffset(Date.Year, Date.Month, Date.Day, hour, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(Date.Year, Date.Month, Date.Day, hour + 1, 0, 0, TimeSpan.Zero),
            name, topic, status, isPaid, price, debt, seriesRule);

    private static AgendaView View(IReadOnlyList<DayLineView> lines, AppLanguage lang = AppLanguage.Uk) =>
        new(lang, Date, lines, [], new Dictionary<Guid, DateTimeOffset>(), null);

    [Fact]
    public void Agenda_MultipleLessons_RendersHeaderSumAndOneLinePerLesson()
    {
        // Arrange
        var lines = new[]
        {
            Line("Іван Петренко", 9, topic: "Present Perfect"),
            Line("Марія К.", 11),
            Line("Олег Д.", 14, debt: 300m),
        };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        var text = message.Text.Split('\n');
        Assert.Equal("☀️ Сьогодні <b>3 уроки</b> — 09:00–15:00", text[0]);
        Assert.Equal($"середа, 5 серпня · {NotificationFormatting.Money(1500m)}", text[1]);
        Assert.Equal(string.Empty, text[2]);
        Assert.Equal("09:00 Іван Петренко · Present Perfect", text[3]);
        Assert.Equal("11:00 Марія К.", text[4]);
        Assert.Equal("14:00 Олег Д. · 💰 борг 300 ₴", text[5]);
    }

    [Fact]
    public void Agenda_HeaderCount_CountsOnlyLiveLessons()
    {
        // Arrange
        var lines = new[]
        {
            Line("Іван", 9),
            Line("Марія", 11),
            Line("Олег", 14, status: LessonStatus.Cancelled),
        };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert — the cancelled lesson still shows a line but does not count in the header.
        Assert.StartsWith("☀️ Сьогодні <b>2 уроки</b>", message.Text);
        Assert.Contains("<s>❌ 14:00 Олег</s>", message.Text);
    }

    [Fact]
    public void Agenda_SumZero_OmitsSumFromSubheader()
    {
        // Arrange
        var lines = new[] { Line("Іван", 9, price: 0m), Line("Марія", 11, price: 0m) };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.DoesNotContain("₴", message.Text.Split('\n')[1]);
    }

    [Fact]
    public void Agenda_MoreThanSixLessons_DropsTopicDebtRuleAndUsesShortName()
    {
        // Arrange
        var lines = Enumerable.Range(9, 7)
            .Select(h => Line(
                "Олександра Вишневецька-Богуславська", h, topic: "Grammar", debt: 100m, seriesRule: "щовівторка"))
            .ToArray();

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.DoesNotContain("Grammar", message.Text);
        Assert.DoesNotContain("борг", message.Text);
        Assert.DoesNotContain("🔁", message.Text);
        Assert.Contains("09:00 Олександра В.-Б.", message.Text);
    }

    [Fact]
    public void Agenda_TwelveLessons_RendersOneLinePerLessonAndCorrectHeaderCount()
    {
        // Arrange
        var lines = Enumerable.Range(8, 12).Select(h => Line($"Student {h}", h)).ToArray();

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.StartsWith("☀️ Сьогодні <b>12 уроків</b>", message.Text);
        Assert.Equal(12, lines.Length);
        foreach (var line in lines)
            Assert.Contains($"{line.StartLocal:HH:mm}", message.Text);
    }

    [Fact]
    public void Agenda_SingleLesson_UsesDedicatedTemplateWithoutList()
    {
        // Arrange
        var lines = new[] { Line("Іван Петренко", 9, topic: "Present Perfect") };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.Equal(
            "☀️ Сьогодні <b>один урок</b> — Іван, <b>09:00</b>\n" +
            "середа, 5 серпня · 09:00–10:00 · Present Perfect",
            message.Text);
    }

    [Fact]
    public void Agenda_SingleLessonLongDoubleBarrelledName_UsesFirstNameNotFullNameInHeader()
    {
        // Arrange — the A3 header budget is ~42 characters (spec §1/§8); a long double-barrelled full
        // name in the header would blow well past it.
        var lines = new[] { Line("Олександра Вишневецька-Богуславська", 9, topic: null) };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.StartsWith("☀️ Сьогодні <b>один урок</b> — Олександра, <b>09:00</b>", message.Text);
        Assert.DoesNotContain("Вишневецька-Богуславська", message.Text);
    }

    [Fact]
    public void Agenda_SingleLessonEmptyTopic_OmitsTopicSuffix()
    {
        // Arrange
        var lines = new[] { Line("Іван Петренко", 9, topic: null) };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.Equal(
            "☀️ Сьогодні <b>один урок</b> — Іван, <b>09:00</b>\n" +
            "середа, 5 серпня · 09:00–10:00",
            message.Text);
    }

    [Fact]
    public void Agenda_AllLessonsCancelled_ShowsFreeHeaderAndStrikethroughLinesWithNoButton()
    {
        // Arrange
        var lines = new[]
        {
            Line("Іван Петренко", 9, status: LessonStatus.Cancelled),
            Line("Марія К.", 11, status: LessonStatus.Cancelled),
        };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.Equal(
            "☀️ Сьогодні <b>вільно</b> — усі 2 уроки скасовано\n" +
            "<s>❌ 09:00 Іван Петренко</s>\n" +
            "<s>❌ 11:00 Марія К.</s>",
            message.Text);
        Assert.Empty(message.Rows);
    }

    [Fact]
    public void Agenda_PriceZero_UsesDedicatedFreeLessonPathWithoutError()
    {
        // Arrange — a single free lesson still renders correctly with no crash on price formatting.
        var lines = new[] { Line("Іван Петренко", 9, price: 0m) };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.Contains("Іван", message.Text);
    }

    [Fact]
    public void Agenda_CompletedLesson_RendersCheckmarkLineOnly()
    {
        // Arrange
        var lines = new[]
        {
            Line("Іван", 9, status: LessonStatus.Completed, topic: "Grammar", debt: 100m),
            Line("Марія", 11),
        };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        Assert.Contains("✅ 09:00 Іван", message.Text);
        Assert.DoesNotContain("Grammar", message.Text);
    }

    [Fact]
    public void Agenda_WithMiniAppUrl_EmitsOpenScheduleButton()
    {
        // Arrange
        var lines = new[] { Line("Іван", 9), Line("Марія", 11) };

        // Act
        var message = Build().Agenda(View(lines));

        // Assert
        var button = Assert.Single(Assert.Single(message.Rows).Buttons);
        Assert.Equal($"https://app.example.com?startapp=schedule-{Date:yyyy-MM-dd}", button.Url);
    }

    [Fact]
    public void Agenda_NoMiniAppUrl_OmitsScheduleButton()
    {
        // Arrange
        var lines = new[] { Line("Іван", 9), Line("Марія", 11) };

        // Act
        var message = Build(miniAppUrl: "").Agenda(View(lines));

        // Assert
        Assert.Empty(message.Rows);
    }

    [Fact]
    public void Agenda_LessonAddedSinceBaseline_AppendsUpdatedLineWithAddedDelta()
    {
        // Arrange
        var baselineId = Guid.NewGuid();
        var addedId = Guid.NewGuid();
        var lines = new[] { Line("Іван", 9, id: baselineId), Line("Марія", 11, id: addedId) };
        var view = new AgendaView(
            AppLanguage.Uk, Date, lines, [baselineId],
            new Dictionary<Guid, DateTimeOffset> { [baselineId] = lines[0].StartLocal.ToUniversalTime() },
            new DateTimeOffset(2026, 8, 5, 14, 20, 0, TimeSpan.Zero));

        // Act
        var message = Build().Agenda(view);

        // Assert
        Assert.Contains("<i>оновлено 14:20 · +1 урок</i>", message.Text);
    }

    [Fact]
    public void Agenda_LessonCancelledSinceBaseline_AppendsUpdatedLineWithCancelledDelta()
    {
        // Arrange
        var id = Guid.NewGuid();
        var lines = new[] { Line("Іван", 9, status: LessonStatus.Cancelled, id: id) };
        var view = new AgendaView(
            AppLanguage.Uk, Date, lines, [id],
            new Dictionary<Guid, DateTimeOffset> { [id] = lines[0].StartLocal.ToUniversalTime() },
            new DateTimeOffset(2026, 8, 5, 14, 20, 0, TimeSpan.Zero));

        // Act
        var message = Build().Agenda(view);

        // Assert
        Assert.Contains("1 скасовано", message.Text);
    }

    [Fact]
    public void Agenda_LessonMovedSinceBaseline_AppendsUpdatedLineWithMovedDelta()
    {
        // Arrange
        var id = Guid.NewGuid();
        var lines = new[] { Line("Іван", 10, id: id) };
        var baselineStart = new DateTimeOffset(Date.Year, Date.Month, Date.Day, 9, 0, 0, TimeSpan.Zero);
        var view = new AgendaView(
            AppLanguage.Uk, Date, lines, [id],
            new Dictionary<Guid, DateTimeOffset> { [id] = baselineStart },
            new DateTimeOffset(2026, 8, 5, 14, 20, 0, TimeSpan.Zero));

        // Act
        var message = Build().Agenda(view);

        // Assert
        Assert.Contains("1 перенесено", message.Text);
    }

    [Fact]
    public void Agenda_NothingChangedSinceBaseline_OmitsUpdatedLine()
    {
        // Arrange
        var id = Guid.NewGuid();
        var lines = new[] { Line("Іван", 9, id: id) };
        var view = new AgendaView(
            AppLanguage.Uk, Date, lines, [id],
            new Dictionary<Guid, DateTimeOffset> { [id] = lines[0].StartLocal.ToUniversalTime() },
            new DateTimeOffset(2026, 8, 5, 14, 20, 0, TimeSpan.Zero));

        // Act
        var message = Build().Agenda(view);

        // Assert
        Assert.DoesNotContain("оновлено", message.Text);
    }

    [Fact]
    public void Agenda_EnglishLocale_RendersEnglishHeader()
    {
        // Arrange
        var lines = new[] { Line("Ivan", 9), Line("Maria", 11) };

        // Act
        var message = Build().Agenda(View(lines, AppLanguage.En));

        // Assert
        Assert.StartsWith("☀️ Today — <b>2 lessons</b>, 09:00–12:00", message.Text);
    }
}
