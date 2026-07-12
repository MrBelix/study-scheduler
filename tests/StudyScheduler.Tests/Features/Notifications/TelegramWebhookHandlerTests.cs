using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Features.Lessons;
using Xunit;
using TgCallbackQuery = Telegram.Bot.Types.CallbackQuery;
using TgUpdate = Telegram.Bot.Types.Update;
using TgUser = Telegram.Bot.Types.User;

namespace StudyScheduler.Tests.Features.Notifications;

public class TelegramWebhookHandlerTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 999;
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly FakeLessonRepository _lessons = new();
    private readonly FakeLessonSeriesRepository _series = new();
    private readonly FakeTutorProfileRepository _profiles = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeNotificationSender _sender = new();
    private readonly TelegramWebhookHandler _sut;

    public TelegramWebhookHandlerTests()
    {
        var overlap = new LessonOverlapChecker(
            _lessons, _series, new SeriesExpansion(_lessons, _series), NullLogger<LessonOverlapChecker>.Instance);
        var patch = new LessonPatchService(_lessons, overlap, _uow, NullLogger<LessonPatchService>.Instance);
        _sut = new TelegramWebhookHandler(
            _lessons, patch, _profiles, _uow, _sender, NullLogger<TelegramWebhookHandler>.Instance);
    }

    private static DateTimeOffset Utc(int day, int hour) => new(2026, 7, day, hour, 0, 0, TimeSpan.Zero);

    private Lesson AddLesson(long tutorId = Tutor)
    {
        var lesson = Lesson.Create(tutorId, Student, Utc(20, 15), 60, 100m, CreatedAt).Value;
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private static TgUpdate Callback(long fromId, string? data) => new()
    {
        CallbackQuery = new TgCallbackQuery
        {
            Id = "cbq-1",
            ChatInstance = "chat-instance",
            From = new TgUser { Id = fromId, FirstName = "T" },
            Data = data,
        },
    };

    [Fact]
    public async Task HandleAsync_CompletedCallback_MarksLessonCompletedAndAnswers()
    {
        // Arrange
        var lesson = AddLesson();

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"c:{lesson.Id:N}"));

        // Assert
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.False(lesson.IsPaid);
        Assert.Single(_sender.Answered);
    }

    [Fact]
    public async Task HandleAsync_PaidCallback_MarksCompletedAndPaid()
    {
        // Arrange
        var lesson = AddLesson();

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"p:{lesson.Id:N}"));

        // Assert
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.True(lesson.IsPaid);
        Assert.Single(_sender.Answered);
    }

    [Fact]
    public async Task HandleAsync_CancelledCallback_MarksLessonCancelled()
    {
        // Arrange
        var lesson = AddLesson();

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"x:{lesson.Id:N}"));

        // Assert
        Assert.Equal(LessonStatus.Cancelled, lesson.Status);
        Assert.Single(_sender.Answered);
    }

    [Fact]
    public async Task HandleAsync_CallbackForAnotherTutorsLesson_DoesNotMutate()
    {
        // Arrange
        var lesson = AddLesson(Tutor);

        // Act — the callback comes from a different tutor than the lesson's owner.
        await _sut.HandleAsync(Callback(OtherTutor, $"c:{lesson.Id:N}"));

        // Assert — ownership scoping reads it as missing; nothing changes and the button is answered.
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        var answered = Assert.Single(_sender.Answered);
        Assert.Equal("Not found", answered.Text);
    }

    [Fact]
    public async Task HandleAsync_MalformedCallbackData_AnswersWithoutMutating()
    {
        // Arrange
        var lesson = AddLesson();

        // Act
        await _sut.HandleAsync(Callback(Tutor, "not-a-valid-payload"));

        // Assert
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        var answered = Assert.Single(_sender.Answered);
        Assert.Equal("?", answered.Text);
    }

    [Fact]
    public async Task HandleAsync_UpdateFromUnreachableTutor_ReEnablesReachability()
    {
        // Arrange
        var profile = TutorProfile.Create(Tutor, London, CreatedAt).Value;
        profile.MarkBotUnreachable();
        _profiles.Items.Add(profile);
        var lesson = AddLesson();

        // Act — any interaction from the tutor whose bot we'd disabled resumes notifications.
        await _sut.HandleAsync(Callback(Tutor, $"c:{lesson.Id:N}"));

        // Assert
        Assert.True(profile.BotReachable);
    }
}
