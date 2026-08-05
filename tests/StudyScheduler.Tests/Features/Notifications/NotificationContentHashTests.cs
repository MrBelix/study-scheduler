using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>
/// The safety net for "the engine silently stopped updating": one test per field that reaches the
/// rendered text or keyboard. A change to any of them must change <see cref="RenderedMessage.ContentHash"/>;
/// leaving a field out means an edit that changes what the tutor sees would stop being issued.
/// </summary>
public class NotificationContentHashTests
{
    private static readonly Guid LessonId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 8, 5);

    private static NotificationRenderer Renderer() =>
        new(Options.Create(new NotificationsOptions { MiniAppUrl = "https://app.example.com" }));

    private static ReminderView ReminderView(
        AppLanguage lang = AppLanguage.Uk, ReminderPhase phase = ReminderPhase.BeforeStart,
        int remindMinutes = 30, string studentName = "Іван Петренко", string? topic = "Grammar",
        int durationMinutes = 60, decimal price = 500m, bool isPaid = false) =>
        new(
            lang, LessonId, phase, remindMinutes, studentName, topic,
            Start, Start.AddMinutes(durationMinutes), durationMinutes, price, isPaid, null, isPaid, Start);

    private static DayLineView DayLine(
        string name = "Іван", LessonStatus status = LessonStatus.Scheduled, decimal price = 500m,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Start, Start.AddMinutes(60), name, null, status, false, price, 0m, null);

    private static AgendaView AgendaView(IReadOnlyList<DayLineView> lines) =>
        new(AppLanguage.Uk, Date, lines, [], new Dictionary<Guid, DateTimeOffset>(), null);

    private static SummaryView SummaryView(
        IReadOnlyList<DayLineView> lines, Guid? focused = null, bool expanded = false, int tomorrow = 0) =>
        new(AppLanguage.Uk, Date, lines, focused, expanded, tomorrow);

    [Fact]
    public void Reminder_StudentNameChanged_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(studentName: "Іван Петренко"));

        // Act
        var b = renderer.Reminder(ReminderView(studentName: "Олег Дорошенко"));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_StudentNameUnchanged_HashUnchanged()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(studentName: "Іван Петренко"));

        // Act
        var b = renderer.Reminder(ReminderView(studentName: "Іван Петренко"));

        // Assert
        Assert.Equal(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_TopicSetToEmpty_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(topic: "Grammar"));

        // Act
        var b = renderer.Reminder(ReminderView(topic: null));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_StartTimeChanged_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView());

        // Act
        var shifted = ReminderView() with { StartLocal = Start.AddMinutes(15), EndLocal = Start.AddMinutes(75) };
        var b = renderer.Reminder(shifted);

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_DurationChanged_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(durationMinutes: 60));

        // Act
        var b = renderer.Reminder(ReminderView(durationMinutes: 90));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_PriceChanged_HashChanges()
    {
        // Arrange — Completed phase renders price in its "not paid" line.
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(phase: ReminderPhase.Completed, price: 500m, isPaid: false));

        // Act
        var b = renderer.Reminder(ReminderView(phase: ReminderPhase.Completed, price: 700m, isPaid: false));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_IsPaidChanged_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(phase: ReminderPhase.Completed, price: 500m, isPaid: false));

        // Act
        var b = renderer.Reminder(ReminderView(phase: ReminderPhase.Completed, price: 500m, isPaid: true));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_StatusPhaseChanged_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(phase: ReminderPhase.BeforeStart));

        // Act
        var b = renderer.Reminder(ReminderView(phase: ReminderPhase.Started));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_LanguageChanged_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(lang: AppLanguage.Uk));

        // Act
        var b = renderer.Reminder(ReminderView(lang: AppLanguage.En));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Reminder_RemindMinutesChanged_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Reminder(ReminderView(remindMinutes: 30));

        // Act
        var b = renderer.Reminder(ReminderView(remindMinutes: 60));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Agenda_LessonAdded_HashChanges()
    {
        // Arrange
        var renderer = Renderer();
        var a = renderer.Agenda(AgendaView([DayLine()]));

        // Act
        var b = renderer.Agenda(AgendaView([DayLine(), DayLine("Марія")]));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Agenda_LessonCancelled_HashChanges()
    {
        // Arrange
        var id = Guid.NewGuid();
        var renderer = Renderer();
        var a = renderer.Agenda(AgendaView([DayLine(id: id), DayLine("Марія")]));

        // Act
        var b = renderer.Agenda(AgendaView([DayLine(status: LessonStatus.Cancelled, id: id), DayLine("Марія")]));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Agenda_LessonMoved_HashChanges()
    {
        // Arrange
        var id = Guid.NewGuid();
        var renderer = Renderer();
        var line = DayLine(id: id);
        var a = renderer.Agenda(AgendaView([line, DayLine("Марія")]));

        // Act
        var moved = line with { StartLocal = Start.AddHours(1), EndLocal = Start.AddHours(2) };
        var b = renderer.Agenda(AgendaView([moved, DayLine("Марія")]));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Summary_LessonMarkedCompleted_HashChanges()
    {
        // Arrange
        var id = Guid.NewGuid();
        var renderer = Renderer();
        var a = renderer.Summary(SummaryView([DayLine(id: id), DayLine("Марія")]));

        // Act
        var b = renderer.Summary(SummaryView([DayLine(status: LessonStatus.Completed, id: id), DayLine("Марія")]));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Summary_Expanded_HashChanges()
    {
        // Arrange
        var lines = Enumerable.Range(0, 8).Select(_ => DayLine()).ToArray();
        var renderer = Renderer();
        var a = renderer.Summary(SummaryView(lines, expanded: false));

        // Act
        var b = renderer.Summary(SummaryView(lines, expanded: true));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Summary_FocusedLessonIdChanged_HashChanges()
    {
        // Arrange
        var first = DayLine();
        var second = DayLine("Марія");
        var renderer = Renderer();
        var a = renderer.Summary(SummaryView([first, second], focused: first.LessonId));

        // Act
        var b = renderer.Summary(SummaryView([first, second], focused: second.LessonId));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Summary_TomorrowLessonsCountChanged_HashChanges()
    {
        // Arrange
        var lines = new[] { DayLine(status: LessonStatus.Completed) };
        var renderer = Renderer();
        var a = renderer.Summary(SummaryView(lines, tomorrow: 0));

        // Act
        var b = renderer.Summary(SummaryView(lines, tomorrow: 3));

        // Assert
        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }
}
