using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// The patch pipeline behind <see cref="LessonService.UpdateAsync"/> — the one seam a lesson is
/// mutated through, whether the app's PATCH or the bot's buttons ask for it.
/// </summary>
public class LessonPatchPipelineTests
{
    private const long Tutor = 555;
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly TutorContext _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeStudentRepository _students;
    private readonly FakeUnitOfWork _uow = new();
    private readonly LessonService _sut;

    public LessonPatchPipelineTests()
    {
        // The pipeline patches whatever row the id resolved to, and its overlap check reads that
        // same scope's calendar — no tutor id travels with the request any more.
        _tenant.SetFromAuthentication(Tutor);
        _lessons = new FakeLessonRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _sut = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now));
    }

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) => new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    private Lesson AddLesson(
        int day, int hour, int duration = 60, LessonStatus status = LessonStatus.Scheduled,
        Guid? studentId = null)
    {
        var lesson = Lesson.Create(
            studentId ?? Student, Utc(day, hour), duration, 100m, CreatedAt).Value.OwnedBy(Tutor);
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    /// <summary>A physical row of a weekly series — the shape the generator writes.</summary>
    private Lesson AddSeriesLesson()
    {
        var monday = new DateOnly(2026, 7, 6);
        var series = LessonSeries.Create(
            Student, WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            monday, CreatedAt).Value.OwnedBy(Tutor);
        _series.Items.Add(series);

        var occurrence = series.GetOccurrences(monday, monday)[0];
        var lesson = Lesson.Create(
            Student, occurrence.StartUtc, 60, 300m, CreatedAt,
            seriesId: series.Id, occurrenceDate: monday).Value.OwnedBy(Tutor);
        _lessons.Items.Add(lesson);
        return lesson;
    }

    /// <summary>
    /// A student of the same tutor the tutor stopped teaching — their schedule was swept when it
    /// happened, so only completed history of theirs can still sit ahead.
    /// </summary>
    private Guid AddArchivedStudent()
    {
        var student = StudyScheduler.Domain.Students.Student
            .Create("Former kid", 300m, CreatedAt).Value.OwnedBy(Tutor);
        student.ChangeStatus(StudentStatus.Archived);
        _students.Items.Add(student);
        return student.Id;
    }

    private static UpdateLessonRequest Patch(
        DateTimeOffset? startUtc = null, int? duration = null, LessonStatus? status = null,
        decimal? price = null, bool? isPaid = null, string? topic = null, string? description = null) =>
        new(startUtc, duration, status, price, isPaid, topic, description);

    [Fact]
    public async Task Cancel_SkipsOverlapCheck_AndSaves()
    {
        var lesson = AddLesson(day: 20, hour: 15);
        AddLesson(day: 20, hour: 15); // an overlapping lesson — irrelevant when cancelling

        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(status: LessonStatus.Cancelled));

        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(LessonStatus.Cancelled, ok.Lesson.Status);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task Reschedule_ToFreeTime_UpdatesTimes()
    {
        var lesson = AddLesson(day: 20, hour: 15);

        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(startUtc: Utc(21, 15)));

        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(Utc(21, 15), ok.Lesson.StartUtc);
        Assert.Equal(Utc(21, 16), ok.Lesson.EndUtc);
    }

    [Fact]
    public async Task Reschedule_ToConflict_ReturnsConflict()
    {
        var lesson = AddLesson(day: 20, hour: 15);
        AddLesson(day: 21, hour: 15); // occupies the target slot

        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(startUtc: Utc(21, 15, 30)));

        Assert.IsType<LessonPatchOutcome.Conflict>(outcome);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task ChangeDuration_OutOfRange_ReturnsValidation()
    {
        var lesson = AddLesson(day: 20, hour: 15);

        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(duration: 601));

        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        Assert.Equal("Lesson.DurationOutOfRange", Assert.Single(validation.Failure.Errors).Code);
    }

    [Fact]
    public async Task UnCancel_RunsOverlapCheck_AndConflicts()
    {
        var cancelled = AddLesson(day: 20, hour: 15, status: LessonStatus.Cancelled);
        AddLesson(day: 20, hour: 15); // scheduled lesson now occupying the slot

        var outcome = await _sut.UpdateAsync(cancelled.Id, Patch(status: LessonStatus.Scheduled));

        Assert.IsType<LessonPatchOutcome.Conflict>(outcome);
    }

    [Fact]
    public async Task UpdateAsync_SeriesLessonRescheduledInsideItsOwnOccurrence_ReturnsOk()
    {
        // Arrange
        var monday = new DateOnly(2026, 7, 6);
        var series = LessonSeries.Create(
            Student, WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), 60, London).Value,
            monday, CreatedAt).Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        var occurrence = series.GetOccurrences(monday, monday)[0];
        // The lesson reaches the patch pipeline as the row its series generated, and a row never
        // collides with itself.
        var generated = Lesson.Create(
            Student, occurrence.StartUtc, 60, 0m, CreatedAt,
            seriesId: series.Id, occurrenceDate: monday).Value.OwnedBy(Tutor);
        _lessons.Items.Add(generated);

        // Act — move it within the span of its own occurrence.
        var outcome = await _sut.UpdateAsync(
            generated.Id, Patch(startUtc: occurrence.StartUtc.AddMinutes(30)));

        // Assert
        Assert.IsType<LessonPatchOutcome.Ok>(outcome);
    }

    [Fact]
    public async Task UpdateAsync_PriceChangedToZero_MarksLessonPaid()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15); // price 100, unpaid

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(price: 0m));

        // Assert
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(0m, ok.Lesson.Price);
        Assert.True(ok.Lesson.IsPaid);
    }

    [Fact]
    public async Task UpdateAsync_PriceZeroWithExplicitUnpaid_KeepsLessonUnpaid()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15);

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(price: 0m, isPaid: false));

        // Assert
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.False(ok.Lesson.IsPaid);
    }

    [Fact]
    public async Task UpdateAsync_CancelAndMarkPaid_ReturnsValidationOnIsPaid()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15);

        // Act — SetPaid sees the post-patch status, so this is rejected, not silently applied.
        var outcome = await _sut.UpdateAsync(
            lesson.Id, Patch(status: LessonStatus.Cancelled, isPaid: true));

        // Assert
        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        var error = Assert.Single(validation.Failure.Errors);
        Assert.Equal("Lesson.CancelledCannotBePaid", error.Code);
        Assert.Equal("IsPaid", error.Field);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_CancelPaidLesson_ClearsPaidFlagAndSaves()
    {
        // Arrange — the student had already paid for this lesson.
        var lesson = AddLesson(day: 20, hour: 15);
        lesson.SetPaid(true);

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(status: LessonStatus.Cancelled));

        // Assert
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(LessonStatus.Cancelled, ok.Lesson.Status);
        Assert.False(ok.Lesson.IsPaid);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_UnCancelAndMarkPaid_Succeeds()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15, status: LessonStatus.Cancelled);

        // Act — the status is applied before the paid flag, so the guard no longer bites.
        var outcome = await _sut.UpdateAsync(
            lesson.Id, Patch(status: LessonStatus.Scheduled, isPaid: true));

        // Assert
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(LessonStatus.Scheduled, ok.Lesson.Status);
        Assert.True(ok.Lesson.IsPaid);
    }

    [Fact]
    public async Task UpdateAsync_CancelCompletedLesson_ReturnsValidationOnStatus()
    {
        // Arrange — a lesson the tutor already recorded as having happened, and been paid for.
        var lesson = AddLesson(day: 20, hour: 15, status: LessonStatus.Completed);
        lesson.SetPaid(true);

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(status: LessonStatus.Cancelled));

        // Assert
        // A completed lesson is a settled fact: cancelling it would erase money already counted.
        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        var error = Assert.Single(validation.Failure.Errors);
        Assert.Equal("Lesson.AlreadyCompleted", error.Code);
        Assert.Equal("Status", error.Field);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.True(lesson.IsPaid);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_RescheduleCompletedLesson_ReturnsValidationOnStartUtc()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15, status: LessonStatus.Completed);

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(startUtc: Utc(21, 15)));

        // Assert
        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        var error = Assert.Single(validation.Failure.Errors);
        Assert.Equal("Lesson.AlreadyCompleted", error.Code);
        Assert.Equal("StartUtc", error.Field);
        Assert.Equal(Utc(20, 15), lesson.StartUtc);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_ChangeDurationOfCompletedLesson_ReturnsValidationOnDurationMinutes()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15, status: LessonStatus.Completed);

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(duration: 90));

        // Assert
        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        var error = Assert.Single(validation.Failure.Errors);
        Assert.Equal("Lesson.AlreadyCompleted", error.Code);
        Assert.Equal("DurationMinutes", error.Field);
        Assert.Equal(60, lesson.DurationMinutes);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_SettleCompletedLesson_ReturnsOk()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15, status: LessonStatus.Completed);

        // Act — the payment of a past lesson is exactly what must keep working.
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(isPaid: true));

        // Assert
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.True(ok.Lesson.IsPaid);
        Assert.Equal(LessonStatus.Completed, ok.Lesson.Status);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_NoteOnCompletedLesson_ReturnsOk()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15, status: LessonStatus.Completed);

        // Act
        var outcome = await _sut.UpdateAsync(
            lesson.Id, Patch(topic: "Quadratics", description: "Homework: p. 42"));

        // Assert — notes describe history rather than change it.
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal("Quadratics", ok.Lesson.Topic);
        Assert.Equal("Homework: p. 42", ok.Lesson.Description);
    }

    [Fact]
    public async Task UpdateAsync_CompletedLessonBackToScheduled_ReturnsOk()
    {
        // Arrange — the tutor recorded the wrong lesson as done.
        var lesson = AddLesson(day: 20, hour: 15, status: LessonStatus.Completed);

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(status: LessonStatus.Scheduled));

        // Assert — undoing a mistaken settle stays open; only cancelling it outright does not.
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(LessonStatus.Scheduled, ok.Lesson.Status);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_FutureCompletedLessonOfAnArchivedStudentBackToScheduled_ReturnsValidationOnStatus()
    {
        // Arrange
        // The only row the archive cascade leaves ahead of an archived student: one recorded as
        // done (as the bot's buttons allow) before the time it was scheduled for.
        var lesson = AddLesson(
            day: 20, hour: 15, status: LessonStatus.Completed, studentId: AddArchivedStudent());

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(status: LessonStatus.Scheduled));

        // Assert
        // Undoing the settle would turn that piece of history into a lesson to be taught for someone
        // nobody teaches any more — a booking in all but name, refused like one.
        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        var error = Assert.Single(validation.Failure.Errors);
        Assert.Equal("Student.Archived", error.Code);
        Assert.Equal("Status", error.Field);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_PastCompletedLessonOfAnArchivedStudentBackToScheduled_ReturnsOk()
    {
        // Arrange — it started at 09:00 today, three hours before now.
        var lesson = AddLesson(
            day: 1, hour: 9, status: LessonStatus.Completed, studentId: AddArchivedStudent());

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(status: LessonStatus.Scheduled));

        // Assert
        // Correcting a settle recorded by mistake schedules nothing: the lesson stays where it
        // already is, in the past, so the archived student is handed no plan.
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(LessonStatus.Scheduled, ok.Lesson.Status);
        Assert.Equal(1, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_PastLessonOfAnArchivedStudentRescheduledIntoTheFuture_ReturnsValidationOnStartUtc()
    {
        // Arrange
        // The cascade sweeps plans, not history, so a row that had already started stays — including
        // one nobody ever settled.
        var lesson = AddLesson(day: 1, hour: 9, studentId: AddArchivedStudent());

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(startUtc: Utc(20, 15)));

        // Assert
        // Dragging it forward would put a lesson to be taught back on the schedule of a student the
        // tutor stopped teaching, which is what archiving emptied.
        var validation = Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        var error = Assert.Single(validation.Failure.Errors);
        Assert.Equal("Student.Archived", error.Code);
        Assert.Equal("StartUtc", error.Field);
        Assert.Equal(Utc(1, 9), lesson.StartUtc);
        Assert.Equal(0, _uow.SaveCount);
    }

    [Fact]
    public async Task UpdateAsync_PastLessonOfAnArchivedStudentRescheduledWithinThePast_ReturnsOk()
    {
        // Arrange — Now is the 1st at noon; both instants are behind it.
        var lesson = AddLesson(day: 1, hour: 9, studentId: AddArchivedStudent());

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(startUtc: Utc(1, 10)));

        // Assert — correcting when a lesson actually happened schedules nothing.
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.Equal(Utc(1, 10), ok.Lesson.StartUtc);
    }

    [Fact]
    public async Task UpdateAsync_PaymentOfAFutureCompletedLessonOfAnArchivedStudent_ReturnsOk()
    {
        // Arrange
        var lesson = AddLesson(
            day: 20, hour: 15, status: LessonStatus.Completed, studentId: AddArchivedStudent());

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(isPaid: true));

        // Assert
        // The refusal above is about re-scheduling alone: the money a completed lesson carries is
        // exactly what the debt dashboard counts, archived student or not.
        var ok = Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.True(ok.Lesson.IsPaid);
        Assert.Equal(LessonStatus.Completed, ok.Lesson.Status);
    }

    [Fact]
    public async Task UpdateAsync_SeriesLessonGivenATopic_LatchesIsCustomized()
    {
        // Arrange
        var lesson = AddSeriesLesson();

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(topic: "Quadratics"));

        // Assert
        // The occurrence now carries a decision of its own, so the generator must never reconsider
        // its date.
        Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.True(lesson.IsCustomized);
    }

    [Fact]
    public async Task UpdateAsync_SeriesLessonCancelled_LatchesIsCustomized()
    {
        // Arrange
        var lesson = AddSeriesLesson();

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(status: LessonStatus.Cancelled));

        // Assert
        // The single cancelled occurrence is the case that matters most: regeneration must not undo it.
        Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.True(lesson.IsCustomized);
    }

    [Fact]
    public async Task UpdateAsync_SeriesLessonWithAFailingPatch_LeavesIsCustomizedUnset()
    {
        // Arrange
        var lesson = AddSeriesLesson();

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(duration: 601));

        // Assert
        // Nothing was decided, so nothing is latched — the flag follows a successful mutation only.
        Assert.IsType<LessonPatchOutcome.Validation>(outcome);
        Assert.False(lesson.IsCustomized);
    }

    [Fact]
    public async Task UpdateAsync_OneOffLesson_LeavesIsCustomizedUnset()
    {
        // Arrange
        var lesson = AddLesson(day: 20, hour: 15);

        // Act
        var outcome = await _sut.UpdateAsync(lesson.Id, Patch(topic: "Anything"));

        // Assert
        // A one-off is nobody's generation candidate; the flag would say nothing about it.
        Assert.IsType<LessonPatchOutcome.Ok>(outcome);
        Assert.False(lesson.IsCustomized);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
