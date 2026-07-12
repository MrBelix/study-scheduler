using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;
using StudyScheduler.Tests.Features.Lessons;
using Xunit;
using TgCallbackQuery = Telegram.Bot.Types.CallbackQuery;
using TgChat = Telegram.Bot.Types.Chat;
using TgMessage = Telegram.Bot.Types.Message;
using TgUpdate = Telegram.Bot.Types.Update;
using TgUser = Telegram.Bot.Types.User;

namespace StudyScheduler.Tests.Features.Notifications;

public class TelegramWebhookHandlerTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 999;
    private const int MessageId = 42;
    private const string OriginalText = "📝 Як пройшов урок з Ann?";
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
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
            _lessons, patch, _profiles, _uow, _sender, new NotificationText(),
            NullLogger<TelegramWebhookHandler>.Instance);
    }

    // "Now" is Jul 15; the default AddLesson slot (Jul 20 15:00 UTC) is in the future relative to it.
    private static DateTimeOffset Utc(int day, int hour) => new(2026, 7, day, hour, 0, 0, TimeSpan.Zero);

    private Lesson AddLesson(long tutorId = Tutor) => AddLessonAt(Utc(20, 15), tutorId);

    private Lesson AddLessonAt(DateTimeOffset startUtc, long tutorId = Tutor)
    {
        var lesson = Lesson.Create(tutorId, Student, startUtc, 60, 100m, CreatedAt).Value;
        _lessons.Items.Add(lesson);
        return lesson;
    }

    private Lesson AddCompletedLesson()
    {
        var lesson = AddLesson();
        lesson.ChangeStatus(LessonStatus.Completed);
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
            Message = new TgMessage
            {
                Id = MessageId,
                Chat = new TgChat { Id = fromId },
                Text = OriginalText,
            },
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
    public async Task HandleAsync_CompleteLesson_EditsMessageWithCompletedMarker()
    {
        // Arrange
        var lesson = AddLesson();

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"c:{lesson.Id:N}"));

        // Assert — the tapped message becomes a record: original text plus the localized Completed
        // marker, keyboard stripped (the fake's edit clears markup). Toast still sent.
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        var edit = Assert.Single(_sender.Edited);
        Assert.Equal(Tutor, edit.ChatId);
        Assert.Equal(MessageId, edit.MessageId);
        Assert.Equal($"{OriginalText}\n\n✅ Проведено", edit.Text);
        Assert.Single(_sender.Answered);
    }

    [Fact]
    public async Task HandleAsync_PaidLesson_EditsMessageWithPaidMarker()
    {
        // Arrange
        var lesson = AddLesson();

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"p:{lesson.Id:N}"));

        // Assert — the 'p' action records both completed and paid.
        var edit = Assert.Single(_sender.Edited);
        Assert.Equal($"{OriginalText}\n\n✅ Проведено · 💰 Оплачено", edit.Text);
    }

    [Fact]
    public async Task HandleAsync_CancelScheduledLesson_CancelsAndEditsMessage()
    {
        // Arrange — a Scheduled lesson whose StartUtc is already in the past (a no-show). Time no longer
        // gates the cancel: only the lesson's status does.
        var lesson = AddLessonAt(Now.AddHours(-2));

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"x:{lesson.Id:N}"));

        // Assert — cancelled regardless of time, and the message is edited into a Cancelled record.
        Assert.Equal(LessonStatus.Cancelled, lesson.Status);
        var edit = Assert.Single(_sender.Edited);
        Assert.Equal(Tutor, edit.ChatId);
        Assert.Equal(MessageId, edit.MessageId);
        Assert.Equal($"{OriginalText}\n\n❌ Скасовано", edit.Text);
        Assert.Single(_sender.Answered);
    }

    [Fact]
    public async Task HandleAsync_CancelCompletedLesson_LeavesLessonAndAnswersToast()
    {
        // Arrange — a lesson the tutor already recorded as Completed must not be silently undone.
        var lesson = AddCompletedLesson();

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"x:{lesson.Id:N}"));

        // Assert — status unchanged, a localized "already completed" toast, and NO message edit.
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        var answered = Assert.Single(_sender.Answered);
        Assert.Equal("Урок уже відмічено проведеним", answered.Text);
        Assert.Empty(_sender.Edited);
    }

    [Fact]
    public async Task HandleAsync_CancelWithoutMessage_CancelsAndSkipsEdit()
    {
        // Arrange — an inaccessible/absent message: the cancel still applies, just no edit is attempted.
        var lesson = AddLesson();
        var update = new TgUpdate
        {
            CallbackQuery = new TgCallbackQuery
            {
                Id = "cbq-1",
                ChatInstance = "chat-instance",
                From = new TgUser { Id = Tutor, FirstName = "T" },
                Data = $"x:{lesson.Id:N}",
            },
        };

        // Act
        await _sut.HandleAsync(update);

        // Assert
        Assert.Equal(LessonStatus.Cancelled, lesson.Status);
        Assert.Empty(_sender.Edited);
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
        Assert.Empty(_sender.Edited);
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
        Assert.Empty(_sender.Edited);
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
