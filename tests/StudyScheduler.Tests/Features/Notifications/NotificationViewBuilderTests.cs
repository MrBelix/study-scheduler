using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Core.Tenancy;
using StudyScheduler.Tests.Features.Lessons;
using StudyScheduler.Tests.Features.Reports;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationViewBuilderTests
{
    private const long Tutor = 555;
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly RecordingTutorScope _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeStudentRepository _students;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentDebtReader _debts;

    public NotificationViewBuilderTests()
    {
        _lessons = new FakeLessonRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _debts = new FakeStudentDebtReader(_lessons);
        _tenant.SetForBackground(Tutor);
    }

    private NotificationViewBuilder Build(DateTimeOffset now) =>
        new(_lessons, _students, _series, _debts, new FixedClock(now));

    private TutorProfile Profile(int? remind = 30)
    {
        var profile = TutorProfile.Create(Tutor, London, CreatedAt).Value;
        profile.UpdateRemindMinutes(remind);
        return profile;
    }

    private Guid AddStudent(string name)
    {
        var student = Student.Create(name, 100m, CreatedAt).Value.OwnedBy(Tutor);
        _students.Items.Add(student);
        return student.Id;
    }

    private Lesson AddLesson(
        Guid studentId, DateTimeOffset startUtc, int duration = 60, decimal price = 500m,
        LessonStatus status = LessonStatus.Scheduled, bool? isPaid = null, string? topic = null,
        Guid? seriesId = null, DateOnly? occurrenceDate = null)
    {
        var lesson = Lesson.Create(
            studentId, startUtc, duration, price, CreatedAt, topic: topic,
            seriesId: seriesId, occurrenceDate: occurrenceDate).Value.OwnedBy(Tutor);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        if (isPaid is { } paid)
            lesson.SetPaid(paid);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    [Fact]
    public async Task BuildReminderAsync_LessonMissingWithPreviousSnapshot_ReturnsRemovedPhase()
    {
        // Arrange
        var lessonId = Guid.NewGuid();
        var previous = new RenderedSnapshot(
            "uk", NotificationKind.Reminder,
            new ReminderSnapshot(
                lessonId, CreatedAt, CreatedAt, 60, 30, "Іван Петренко", null, 500m, false,
                LessonStatus.Scheduled, "BeforeStart"),
            null);

        // Act
        var view = await Build(CreatedAt).BuildReminderAsync(lessonId, Profile(), previous, CreatedAt, CancellationToken.None);

        // Assert
        Assert.NotNull(view);
        Assert.Equal(ReminderPhase.Removed, view!.Phase);
    }

    [Fact]
    public async Task BuildReminderAsync_LessonMissingNoPrevious_ReturnsNull()
    {
        // Arrange
        var lessonId = Guid.NewGuid();

        // Act
        var view = await Build(CreatedAt).BuildReminderAsync(lessonId, Profile(), null, CreatedAt, CancellationToken.None);

        // Assert
        Assert.Null(view);
    }

    [Fact]
    public async Task BuildReminderAsync_NoPreviousSnapshot_UsesProfileRemindMinutes()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, CreatedAt.AddHours(1));
        var profile = Profile(remind: 45);

        // Act
        var view = await Build(CreatedAt).BuildReminderAsync(lesson.Id, profile, null, CreatedAt, CancellationToken.None);

        // Assert
        Assert.Equal(45, view!.RemindMinutes);
    }

    [Fact]
    public async Task BuildReminderAsync_PreviousSnapshotExists_FreezesRemindMinutesFromSnapshot()
    {
        // Arrange — the profile has since changed its lead time to 60, but the armed reminder keeps 30.
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, CreatedAt.AddHours(1));
        var previous = new RenderedSnapshot(
            "uk", NotificationKind.Reminder,
            new ReminderSnapshot(
                lesson.Id, lesson.StartUtc, lesson.StartUtc, 60, 30, "Ann", null, 500m, false,
                LessonStatus.Scheduled, "BeforeStart"),
            null);

        // Act
        var view = await Build(CreatedAt).BuildReminderAsync(
            lesson.Id, Profile(remind: 60), previous, CreatedAt, CancellationToken.None);

        // Assert
        Assert.Equal(30, view!.RemindMinutes);
    }

    [Fact]
    public async Task BuildReminderAsync_StartMovedFromPreviousSnapshot_ReturnsMovedPhase()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        var originalStart = CreatedAt.AddHours(1);
        var lesson = AddLesson(studentId, originalStart.AddHours(2)); // rescheduled since the snapshot
        var previous = new RenderedSnapshot(
            "uk", NotificationKind.Reminder,
            new ReminderSnapshot(
                lesson.Id, originalStart, originalStart, 60, 30, "Ann", null, 500m, false,
                LessonStatus.Scheduled, "BeforeStart"),
            null);

        // Act
        var view = await Build(CreatedAt).BuildReminderAsync(lesson.Id, Profile(), previous, CreatedAt, CancellationToken.None);

        // Assert
        Assert.Equal(ReminderPhase.Moved, view!.Phase);
        Assert.Equal(originalStart, view.OriginalStartLocal);
    }

    [Fact]
    public async Task BuildReminderAsync_CancelledLessonWithPreviousPaidSnapshot_SetsWasPaidBeforeChangeTrue()
    {
        // Arrange — the domain cleared IsPaid on cancel; the previous snapshot remembers it was paid.
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, CreatedAt.AddHours(1), price: 500m, status: LessonStatus.Cancelled);
        var previous = new RenderedSnapshot(
            "uk", NotificationKind.Reminder,
            new ReminderSnapshot(
                lesson.Id, lesson.StartUtc, lesson.StartUtc, 60, 30, "Ann", null, 500m, true,
                LessonStatus.Scheduled, "BeforeStart"),
            null);

        // Act
        var view = await Build(CreatedAt).BuildReminderAsync(lesson.Id, Profile(), previous, CreatedAt, CancellationToken.None);

        // Assert
        Assert.Equal(ReminderPhase.Cancelled, view!.Phase);
        Assert.True(view.WasPaidBeforeChange);
        Assert.False(view.IsPaid); // the domain's own flag is already cleared
    }

    [Fact]
    public async Task BuildReminderAsync_NowAtOrAfterStart_ReturnsStartedPhase()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, CreatedAt);

        // Act
        var view = await Build(CreatedAt).BuildReminderAsync(lesson.Id, Profile(), null, CreatedAt, CancellationToken.None);

        // Assert
        Assert.Equal(ReminderPhase.Started, view!.Phase);
    }

    [Fact]
    public async Task BuildAgendaAsync_LessonAcrossMidnight_LandsOnItsStartLocalDate()
    {
        // Arrange — 23:30 London start on Aug 5, ending 00:30 on Aug 6; belongs to Aug 5's agenda.
        var studentId = AddStudent("Ann");
        var startUtc = WallClock.ToUtc(new DateOnly(2026, 8, 5), new TimeOnly(23, 30), London);
        AddLesson(studentId, startUtc);

        // Act
        var view = await Build(startUtc).BuildAgendaAsync(
            new DateOnly(2026, 8, 5), Profile(), null, startUtc, CancellationToken.None);

        // Assert
        Assert.Single(view.Lines);

        // Act — the next day's agenda must NOT also claim it.
        var nextDay = await Build(startUtc).BuildAgendaAsync(
            new DateOnly(2026, 8, 6), Profile(), null, startUtc, CancellationToken.None);

        // Assert
        Assert.Empty(nextDay.Lines);
    }

    [Fact]
    public async Task BuildAgendaAsync_StudentWithHistoricDebt_LineCarriesAggregateNotLessonPrice()
    {
        // Arrange — an old completed-unpaid lesson (the aggregate debt) plus today's scheduled lesson.
        var studentId = AddStudent("Ann");
        AddLesson(studentId, CreatedAt, price: 900m, status: LessonStatus.Completed, isPaid: false);

        var todayStart = WallClock.ToUtc(new DateOnly(2026, 8, 5), new TimeOnly(9, 0), London);
        AddLesson(studentId, todayStart, price: 100m);

        // Act
        var view = await Build(todayStart).BuildAgendaAsync(
            new DateOnly(2026, 8, 5), Profile(), null, todayStart, CancellationToken.None);

        // Assert — the line's debt is the student's aggregate (900), not today's lesson price (100).
        var line = Assert.Single(view.Lines);
        Assert.Equal(900m, line.StudentDebt);
        Assert.Equal(100m, line.Price);
    }

    [Fact]
    public async Task BuildAgendaAsync_SeriesLessonWithinSixLines_AttachesLocalizedWeeklyRule()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        var pattern = WeeklyPattern.Create(Weekdays.Wednesday, new TimeOnly(9, 0), 60, London).Value;
        var s = LessonSeries.Create(studentId, pattern, new DateOnly(2026, 8, 5), CreatedAt).Value.OwnedBy(Tutor);
        _series.Items.Add(s);

        var startUtc = WallClock.ToUtc(new DateOnly(2026, 8, 5), new TimeOnly(9, 0), London);
        AddLesson(studentId, startUtc, seriesId: s.Id, occurrenceDate: new DateOnly(2026, 8, 5));

        // Act
        var view = await Build(startUtc).BuildAgendaAsync(
            new DateOnly(2026, 8, 5), Profile(), null, startUtc, CancellationToken.None);

        // Assert
        Assert.Equal("щосереди", Assert.Single(view.Lines).SeriesRule);
    }

    [Fact]
    public async Task BuildSummaryAsync_TomorrowLessonsCount_ExcludesCancelledLessons()
    {
        // Arrange
        var studentId = AddStudent("Ann");
        var today = new DateOnly(2026, 8, 5);
        var tomorrowStart1 = WallClock.ToUtc(today.AddDays(1), new TimeOnly(9, 0), London);
        var tomorrowStart2 = WallClock.ToUtc(today.AddDays(1), new TimeOnly(11, 0), London);
        AddLesson(studentId, tomorrowStart1);
        AddLesson(studentId, tomorrowStart2, status: LessonStatus.Cancelled);

        // Act
        var view = await Build(tomorrowStart1).BuildSummaryAsync(
            today, Profile(), null, false, tomorrowStart1, CancellationToken.None);

        // Assert
        Assert.Equal(1, view.TomorrowLessonsCount);
    }

    [Fact]
    public async Task BuildAgendaAsync_DeltaUnchangedSincePreviousRender_FreezesUpdatedAtAcrossTicks()
    {
        // Arrange — a lesson already cancelled against the baseline when the first render happens; the
        // second tick, a minute later, sees the exact same delta.
        var studentId = AddStudent("Ann");
        var todayStart = WallClock.ToUtc(new DateOnly(2026, 8, 5), new TimeOnly(9, 0), London);
        var lesson = AddLesson(studentId, todayStart, status: LessonStatus.Cancelled);
        var baseline = new RenderedSnapshot(
            "uk", NotificationKind.DayAgenda, null,
            new DaySnapshot(
                new DateOnly(2026, 8, 5), [], [lesson.Id],
                new Dictionary<Guid, DateTimeOffset> { [lesson.Id] = lesson.StartUtc }));
        var renderer = new NotificationRenderer(Options.Create(new NotificationsOptions()));

        var firstView = await Build(todayStart).BuildAgendaAsync(
            new DateOnly(2026, 8, 5), Profile(), baseline, todayStart, CancellationToken.None);
        var firstMessage = renderer.Agenda(firstView);

        // Act — nothing else changes; only the clock moves forward a minute.
        var secondView = await Build(todayStart.AddMinutes(1)).BuildAgendaAsync(
            new DateOnly(2026, 8, 5), Profile(), firstMessage.Snapshot, todayStart.AddMinutes(1),
            CancellationToken.None);

        // Assert — the "оновлено" instant did not advance, so the message would not need a fresh edit.
        Assert.Equal(firstView.UpdatedAtLocal, secondView.UpdatedAtLocal);
    }

    [Fact]
    public async Task BuildAgendaAsync_DeltaChangesAgain_AdvancesUpdatedAt()
    {
        // Arrange — one lesson already cancelled against the baseline; a second, previously untouched
        // lesson is cancelled before the next tick, so the delta genuinely grows.
        var studentId = AddStudent("Ann");
        var today = new DateOnly(2026, 8, 5);
        var firstStart = WallClock.ToUtc(today, new TimeOnly(9, 0), London);
        var secondStart = WallClock.ToUtc(today, new TimeOnly(11, 0), London);
        var first = AddLesson(studentId, firstStart, status: LessonStatus.Cancelled);
        var second = AddLesson(studentId, secondStart);
        var baseline = new RenderedSnapshot(
            "uk", NotificationKind.DayAgenda, null,
            new DaySnapshot(
                today, [], [first.Id, second.Id],
                new Dictionary<Guid, DateTimeOffset> { [first.Id] = first.StartUtc, [second.Id] = second.StartUtc }));
        var renderer = new NotificationRenderer(Options.Create(new NotificationsOptions()));

        var firstView = await Build(firstStart).BuildAgendaAsync(today, Profile(), baseline, firstStart, CancellationToken.None);
        var firstMessage = renderer.Agenda(firstView);

        // Act
        second.ChangeStatus(LessonStatus.Cancelled);
        var secondView = await Build(firstStart.AddMinutes(1)).BuildAgendaAsync(
            today, Profile(), firstMessage.Snapshot, firstStart.AddMinutes(1), CancellationToken.None);

        // Assert
        Assert.NotEqual(firstView.UpdatedAtLocal, secondView.UpdatedAtLocal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
