using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class TelegramWebhookHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private const long TutorId = 555;

    private sealed class Harness
    {
        public FakeLessonRepository Lessons { get; } = new();
        public FakeLessonSeriesRepository Series { get; } = new();
        public FakeStudentRepository Students { get; } = new();
        public FakeTutorProfileRepository Profiles { get; } = new();
        public FakeUnitOfWork Uow { get; } = new();
        public FakeTelegramBotClient Bot { get; } = new();

        public TelegramWebhookHandler CreateHandler() => new(
            Lessons,
            Series,
            Students,
            Profiles,
            Uow,
            new LessonMaterializer(
                Lessons,
                new SeriesExpansion(Lessons, Series),
                Students,
                new FixedTimeProvider(Now),
                NullLogger<LessonMaterializer>.Instance),
            Bot,
            NullLogger<TelegramWebhookHandler>.Instance);
    }

    private static TelegramUpdate Update(long fromUserId, string callbackData) => new(
        new TelegramCallbackQuery(
            "cb-1",
            new TelegramUser { Id = fromUserId },
            new TelegramCallbackMessage(42, new TelegramChat(fromUserId)),
            callbackData));

    [Fact]
    public async Task Paid_action_marks_lesson_completed_and_paid()
    {
        var harness = new Harness();
        var student = Student.Create(TutorId, "Alice", 100m, Now.AddDays(-10)).Value;
        harness.Students.Students.Add(student);
        var lesson = Lesson.Create(TutorId, student.Id, Now.AddMinutes(-70), 60, 100m, Now.AddDays(-1)).Value;
        harness.Lessons.Lessons.Add(lesson);

        await harness.CreateHandler().HandleAsync(
            Update(TutorId, FollowUpCallback.Format(FollowUpAction.Paid, lesson.Id, null, null)),
            CancellationToken.None);

        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.True(lesson.IsPaid);
        Assert.Same(lesson, Assert.Single(harness.Lessons.Updated));
        Assert.Equal(1, harness.Uow.SaveCount);
        Assert.Single(harness.Bot.AnsweredCallbacks);
        Assert.Single(harness.Bot.EditedMessages);
    }

    [Fact]
    public async Task Foreign_tutors_callback_mutates_nothing_and_still_answers_the_callback()
    {
        var harness = new Harness();
        var lesson = Lesson.Create(TutorId, Guid.NewGuid(), Now.AddMinutes(-70), 60, 100m, Now.AddDays(-1)).Value;
        harness.Lessons.Lessons.Add(lesson);

        await harness.CreateHandler().HandleAsync(
            Update(999, FollowUpCallback.Format(FollowUpAction.Paid, lesson.Id, null, null)),
            CancellationToken.None);

        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        Assert.False(lesson.IsPaid);
        Assert.Empty(harness.Lessons.Added);
        Assert.Empty(harness.Lessons.Updated);
        Assert.Equal(0, harness.Uow.SaveCount);
        // The button spinner must not hang forever, so the callback is still answered.
        Assert.Single(harness.Bot.AnsweredCallbacks);
        Assert.Empty(harness.Bot.EditedMessages);
    }

    [Fact]
    public async Task Double_tap_race_discards_the_lost_insert_and_reapplies_to_the_winning_row()
    {
        var harness = new Harness();
        var studentId = Guid.NewGuid();
        var occurrenceDate = new DateOnly(2026, 7, 8); // a Wednesday
        var series = LessonSeries.Create(
            TutorId, studentId, new DateOnly(2026, 6, 1), Weekdays.Wednesday,
            new TimeOnly(10, 0), 60, TimeZoneInfo.Utc, Now.AddMonths(-2), price: 100m).Value;
        harness.Series.Series.Add(series);

        // First resolve: no physical row yet → the handler materializes and inserts.
        harness.Lessons.SeriesOccurrenceResults.Enqueue(null);
        // The insert loses the unique-index race; the concurrent tap's row is the winner.
        var winner = Lesson.Create(
            TutorId, studentId, Now.AddMinutes(-70), 60, 100m, Now,
            seriesId: series.Id, occurrenceDate: occurrenceDate).Value;
        harness.Lessons.SeriesOccurrenceResults.Enqueue(winner);
        harness.Uow.SaveFailures.Enqueue(new DbUpdateException(
            "duplicate", SqlExceptionFactory.Create(2601)));

        await harness.CreateHandler().HandleAsync(
            Update(TutorId, FollowUpCallback.Format(FollowUpAction.Paid, null, series.Id, occurrenceDate)),
            CancellationToken.None);

        Assert.Equal(1, harness.Uow.DiscardCount);
        Assert.Equal(2, harness.Uow.SaveCount); // failed insert + successful re-apply
        Assert.Equal(LessonStatus.Completed, winner.Status);
        Assert.True(winner.IsPaid);
        Assert.Same(winner, Assert.Single(harness.Lessons.Updated));
        Assert.Single(harness.Bot.AnsweredCallbacks);
    }
}

/// <summary>
/// Builds a real <see cref="SqlException"/> with a chosen error number. The type has no public
/// constructor, but the duplicate-key catch filters check <c>SqlException.Number</c>, so tests
/// need the real type — assembled through the same internal factory the driver uses.
/// </summary>
internal static class SqlExceptionFactory
{
    public static SqlException Create(int number)
    {
        // SqlError's internal constructors all start with "int infoNumber"; fill the rest
        // with neutral defaults.
        var errorCtor = typeof(SqlError)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(c => c.GetParameters() is [{ ParameterType.Name: "Int32" }, ..]);
        var numberSet = false;
        var args = errorCtor.GetParameters().Select(p =>
        {
            if (p.ParameterType == typeof(int) && !numberSet)
            {
                numberSet = true;
                return (object?)number;
            }

            if (p.ParameterType == typeof(string))
                return "";
            return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }).ToArray();
        var error = errorCtor.Invoke(args);

        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(collection, [error]);

        var exception = (SqlException)typeof(SqlException)
            .GetMethod(
                "CreateException",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(SqlErrorCollection), typeof(string)])!
            .Invoke(null, [collection, "16.0"])!;

        Assert.Equal(number, exception.Number); // guard against driver internals shifting
        return exception;
    }
}
