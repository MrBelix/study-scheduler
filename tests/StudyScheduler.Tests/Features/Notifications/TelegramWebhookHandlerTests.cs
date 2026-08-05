using Microsoft.Extensions.Logging.Abstractions;
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
using TgCallbackQuery = Telegram.Bot.Types.CallbackQuery;
using TgChat = Telegram.Bot.Types.Chat;
using TgMessage = Telegram.Bot.Types.Message;
using TgUpdate = Telegram.Bot.Types.Update;
using TgUser = Telegram.Bot.Types.User;

namespace StudyScheduler.Tests.Features.Notifications;

/// <summary>
/// The evening summary's in-message state machine. Every tap is answered by re-rendering the SAME
/// message from current data — a marked lesson stays in the body with a tick and merely stops being a
/// button — and every mutation goes through the one <c>LessonService</c> seam the app's PATCH uses.
/// The webhook is anonymous, so the update's sender is the only identity there is: these tests read
/// through the very scope the payload establishes.
/// </summary>
public class TelegramWebhookHandlerTests
{
    private const long Tutor = 555;
    private const long OtherTutor = 999;
    private const int MessageId = 42;

    /// <summary>2026-08-05 is a Wednesday; London is on BST (UTC+1), so 09:00 local is 08:00 UTC.</summary>
    private static readonly DateOnly Day = new(2026, 8, 5);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>19:00 UTC = 20:00 London — the evening the summary goes out.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 19, 0, 0, TimeSpan.Zero);

    private readonly RecordingTutorScope _tenant = new();
    private readonly FakeLessonRepository _lessons;
    private readonly FakeStudentRepository _students;
    private readonly FakeLessonSeriesRepository _series;
    private readonly FakeTutorProfileRepository _profiles;
    private readonly FakeNotificationDispatchRepository _dispatches;
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeNotificationSender _sender = new();
    private readonly TelegramWebhookHandler _sut;

    public TelegramWebhookHandlerTests()
    {
        // The webhook is anonymous: its scope has no tenant until the update payload names one, and
        // the repositories read through that very scope.
        _lessons = new FakeLessonRepository(_tenant);
        _students = new FakeStudentRepository(_tenant);
        _series = new FakeLessonSeriesRepository(_tenant);
        _profiles = new FakeTutorProfileRepository(_tenant);
        _dispatches = new FakeNotificationDispatchRepository(_tenant);

        // A button tap goes through the very same façade the app's PATCH does.
        var service = LessonServiceFactory.Create(
            _tenant, _lessons, _series, _students, _uow, new FixedClock(Now), _profiles);

        // No mini-app url configured, so every url button is omitted and the keyboard is callbacks only.
        var renderer = new NotificationRenderer(Options.Create(new NotificationsOptions()));
        var views = new NotificationViewBuilder(
            _lessons, _students, _series, new FakeStudentDebtReader(_lessons), new FixedClock(Now));

        _sut = new TelegramWebhookHandler(
            service, _profiles, _dispatches, views, renderer, _sender, _uow, _tenant,
            new FixedClock(Now), NullLogger<TelegramWebhookHandler>.Instance);
    }

    private TutorProfile AddProfile(long tutorId = Tutor)
    {
        var profile = TutorProfile.Create(tutorId, London, CreatedAt).Value;
        profile.UpdateDaySummary(true);
        _profiles.Items.Add(profile);
        return profile;
    }

    private Guid AddStudent(string name, long tutorId = Tutor)
    {
        // Nothing above the database assigns ownership, and the scope has no tenant until an update
        // arrives — so fixture rows are stamped the way persistence stamps them.
        var student = Student.Create(name, 100m, CreatedAt).Value.OwnedBy(tutorId);
        _students.Items.Add(student);
        return student.Id;
    }

    private Lesson AddLesson(
        Guid studentId,
        int localHour,
        decimal price = 500m,
        LessonStatus status = LessonStatus.Scheduled,
        bool paid = false,
        Guid? seriesId = null,
        long tutorId = Tutor)
    {
        var startUtc = WallClock.ToUtc(Day, new TimeOnly(localHour, 0), London);
        var lesson = Lesson.Create(
                studentId, startUtc, 60, price, CreatedAt,
                seriesId: seriesId, occurrenceDate: seriesId is null ? null : Day)
            .Value.OwnedBy(tutorId);

        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        if (paid)
            lesson.SetPaid(true);

        _lessons.Items.Add(lesson);
        return lesson;
    }

    /// <summary>A weekly Wednesday series plus the occurrence row its generator wrote for <see cref="Day"/>.</summary>
    private Lesson AddSeriesLesson(Guid studentId, int localHour)
    {
        var series = LessonSeries.Create(
                studentId, WeeklyPattern.Create(Weekdays.Wednesday, new TimeOnly(localHour, 0), 60, London).Value,
                Day, CreatedAt)
            .Value.OwnedBy(Tutor);
        _series.Items.Add(series);
        return AddLesson(studentId, localHour, seriesId: series.Id);
    }

    /// <summary>The evening summary's own dispatch row, as the planner recorded it.</summary>
    private NotificationDispatch AddSummaryDispatch(int messageId = MessageId)
    {
        var dispatch = NotificationDispatch
            .ForDay(NotificationKind.DaySummary, Day, Tutor, messageId, RenderedSnapshot.Empty, "stale",
                Now.AddHours(5), Now)
            .OwnedBy(Tutor);
        _dispatches.Items.Add(dispatch);
        return dispatch;
    }

    private NotificationDispatch AddReminderDispatch(Lesson lesson)
    {
        var dispatch = NotificationDispatch
            .ForReminder(lesson.Id, Tutor, MessageId, RenderedSnapshot.Empty, "stale", lesson.EndUtc, Now)
            .OwnedBy(Tutor);
        _dispatches.Items.Add(dispatch);
        return dispatch;
    }

    private static TgUpdate Callback(long fromId, string? data, int messageId = MessageId) => new()
    {
        CallbackQuery = new TgCallbackQuery
        {
            Id = "cbq-1",
            ChatInstance = "chat-instance",
            From = new TgUser { Id = fromId, FirstName = "T" },
            Data = data,
            Message = new TgMessage
            {
                Id = messageId,
                Chat = new TgChat { Id = fromId },
                Text = "🌙 Лишилось відмітити",
            },
        },
    };

    private static IReadOnlyList<NotificationButton> ButtonsOf(FakeNotificationSender.EditedMessage edit) =>
        [.. edit.Rows.SelectMany(r => r.Buttons)];

    [Fact]
    public async Task HandleAsync_FocusCallback_EditsIntoTheMarkStepWithoutMutating()
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, 9);
        AddLesson(studentId, 11);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"s:{lesson.Id:N}"));

        // Assert — the step-2 form for that one lesson: two rows, four buttons, all about it.
        var edit = Assert.Single(_sender.Edited);
        Assert.Equal(2, edit.Rows.Count);
        Assert.Equal(
            [$"c:{lesson.Id:N}", $"p:{lesson.Id:N}", $"x:{lesson.Id:N}", "b:"],
            ButtonsOf(edit).Select(b => b.CallbackData));
        Assert.Contains("09:00 · Ann", edit.Text);

        // Assert — a view change mutates nothing and says nothing.
        Assert.All(_lessons.Items, l => Assert.Equal(LessonStatus.Scheduled, l.Status));
        Assert.Null(Assert.Single(_sender.Answered).Text);
    }

    [Fact]
    public async Task HandleAsync_BackCallback_EditsBackIntoTheList()
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var first = AddLesson(studentId, 9);
        var second = AddLesson(studentId, 11);

        // Act
        await _sut.HandleAsync(Callback(Tutor, "b:"));

        // Assert — the list form again: one button per unmarked lesson plus the trailing "all done".
        var edit = Assert.Single(_sender.Edited);
        Assert.Equal(
            [$"s:{first.Id:N}", $"s:{second.Id:N}", $"a:{Day:yyyy-MM-dd}"],
            ButtonsOf(edit).Select(b => b.CallbackData));
        Assert.Null(Assert.Single(_sender.Answered).Text);
    }

    [Fact]
    public async Task HandleAsync_ExpandCallback_ShowsEveryUnmarkedLesson()
    {
        // Arrange — seven unmarked lessons: the collapsed list would show six plus an expander.
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        for (var hour = 9; hour <= 15; hour++)
            AddLesson(studentId, hour);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"m:{Day:yyyy-MM-dd}"));

        // Assert — all seven, no expander, trailing "all done" intact.
        var edit = Assert.Single(_sender.Edited);
        var callbacks = ButtonsOf(edit).Select(b => b.CallbackData).ToList();
        Assert.Equal(7, callbacks.Count(c => c!.StartsWith("s:", StringComparison.Ordinal)));
        Assert.DoesNotContain(callbacks, c => c!.StartsWith("m:", StringComparison.Ordinal));
        Assert.Equal($"a:{Day:yyyy-MM-dd}", callbacks[^1]);
    }

    [Fact]
    public async Task HandleAsync_MarkAllCallback_CompletesEveryScheduledLessonAndReportsTheCount()
    {
        // Arrange — one lesson is already marked, so only the remaining two are the bot's business.
        AddProfile();
        var dispatch = AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var already = AddLesson(studentId, 9, status: LessonStatus.Cancelled);
        var second = AddLesson(studentId, 11);
        var third = AddLesson(studentId, 13);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"a:{Day:yyyy-MM-dd}"));

        // Assert
        Assert.Equal(LessonStatus.Completed, second.Status);
        Assert.Equal(LessonStatus.Completed, third.Status);
        Assert.Equal(LessonStatus.Cancelled, already.Status);
        Assert.Equal("Готово — 2 уроки відмічено", Assert.Single(_sender.Answered).Text);

        // Assert — nothing is left unmarked, so the day closes and the row stops being live.
        Assert.Empty(Assert.Single(_sender.Edited).Rows);
        Assert.Equal(DispatchState.Retracted, dispatch.State);
    }

    [Fact]
    public async Task HandleAsync_CompletedCallbackOnASummary_KeepsTheRowInTheBodyAndDropsItsButton()
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var marked = AddLesson(studentId, 9);
        var rest = AddLesson(studentId, 11);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"c:{marked.Id:N}"));

        // Assert
        Assert.Equal(LessonStatus.Completed, marked.Status);
        Assert.False(marked.IsPaid);
        Assert.Equal("Урок відмічено", Assert.Single(_sender.Answered).Text);

        // Assert — the list does not empty as it is used: the marked lesson stays in the body with a
        // tick and merely stops being a button.
        var edit = Assert.Single(_sender.Edited);
        Assert.Contains("✅ 09:00 Ann", edit.Text);
        Assert.Equal(
            [$"s:{rest.Id:N}", $"a:{Day:yyyy-MM-dd}"],
            ButtonsOf(edit).Select(b => b.CallbackData));
    }

    [Fact]
    public async Task HandleAsync_PaidCallbackOnASummary_MarksTheBodyRowAsPaid()
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var marked = AddLesson(studentId, 9);
        AddLesson(studentId, 11);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"p:{marked.Id:N}"));

        // Assert
        Assert.Equal(LessonStatus.Completed, marked.Status);
        Assert.True(marked.IsPaid);
        Assert.Equal("Проведено й оплачено", Assert.Single(_sender.Answered).Text);
        Assert.Contains("✅ 09:00 Ann · 💰 оплачено", Assert.Single(_sender.Edited).Text);
    }

    [Fact]
    public async Task HandleAsync_LastUnmarkedLessonMarked_RendersTheClosedDayAndRetractsTheDispatch()
    {
        // Arrange
        AddProfile();
        var dispatch = AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        AddLesson(studentId, 9, status: LessonStatus.Completed, paid: true);
        var last = AddLesson(studentId, 11);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"p:{last.Id:N}"));

        // Assert — the terminal form carries no keyboard, and the row stops being the live message.
        var edit = Assert.Single(_sender.Edited);
        Assert.Empty(edit.Rows);
        Assert.Contains("🌙", edit.Text);
        Assert.Equal(DispatchState.Retracted, dispatch.State);
    }

    [Fact]
    public async Task HandleAsync_FocusCallbackOnAFreeLesson_OmitsThePaidButton()
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var free = AddLesson(studentId, 9, price: 0m);
        AddLesson(studentId, 11);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"s:{free.Id:N}"));

        // Assert — nothing is owed, so there is nothing to mark paid.
        var edit = Assert.Single(_sender.Edited);
        Assert.Equal(
            [$"c:{free.Id:N}", $"x:{free.Id:N}", "b:"],
            ButtonsOf(edit).Select(b => b.CallbackData));
        Assert.Contains("безкоштовно", edit.Text);
    }

    [Fact]
    public async Task HandleAsync_CancelCallbackOnACompletedLesson_AnswersAlreadyChangedAndLeavesTheMessage()
    {
        // Arrange — a lesson the tutor already recorded as Completed must not be silently undone.
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var settled = AddLesson(studentId, 9, status: LessonStatus.Completed);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"x:{settled.Id:N}"));

        // Assert — status untouched, the toast says why, and the message state does not change.
        Assert.Equal(LessonStatus.Completed, settled.Status);
        Assert.Equal("Урок уже змінили в застосунку", Assert.Single(_sender.Answered).Text);
        Assert.Empty(_sender.Edited);
    }

    [Fact]
    public async Task HandleAsync_CancelCallbackOnAReminder_RerendersTheReminderIntoItsCancelledForm()
    {
        // Arrange — the reminder's own dispatch names a lesson, not a date, so the reminder is what is
        // re-rendered.
        AddProfile();
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, 20);
        AddReminderDispatch(lesson);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"x:{lesson.Id:N}"));

        // Assert
        Assert.Equal(LessonStatus.Cancelled, lesson.Status);
        Assert.Equal("Урок скасовано", Assert.Single(_sender.Answered).Text);

        // Assert — a cancelled reminder is a record, not an offer: no keyboard is left on it.
        var edit = Assert.Single(_sender.Edited);
        Assert.Empty(edit.Rows);
        Assert.Contains("Ann", edit.Text);
    }

    [Fact]
    public async Task HandleAsync_MarkCallbackOnASeriesOccurrence_LatchesIsCustomized()
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var occurrence = AddSeriesLesson(studentId, 9);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"p:{occurrence.Id:N}"));

        // Assert
        // "Проведено · Оплачено" is a per-lesson fact about a real lesson, exactly like the app's own
        // patch — it must survive the schedule being regenerated around it.
        Assert.Equal(LessonStatus.Completed, occurrence.Status);
        Assert.True(occurrence.IsCustomized);
    }

    [Fact]
    public async Task HandleAsync_CallbackForAnotherTutorsLesson_CannotReachIt()
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, 9);

        // Act — the callback comes from a different tutor than the lesson's owner.
        await _sut.HandleAsync(Callback(OtherTutor, $"c:{lesson.Id:N}"));

        // Assert
        // The payload cannot name a tenant: the sender is the tenant, so ownership scoping reads the
        // lesson as missing and nothing changes.
        Assert.Equal(OtherTutor, _tenant.CurrentTutorTelegramId);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        Assert.Equal("Цього уроку більше немає", Assert.Single(_sender.Answered).Text);
        Assert.Empty(_sender.Edited);
    }

    [Fact]
    public async Task HandleAsync_CallbackOnAMessageWithNoDispatch_MutatesWithoutEditing()
    {
        // Arrange — a pre-redesign message: the mutation still applies, it just cannot be repainted.
        AddProfile();
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, 9);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"c:{lesson.Id:N}", messageId: 7777));

        // Assert
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Equal("Урок відмічено", Assert.Single(_sender.Answered).Text);
        Assert.Empty(_sender.Edited);
    }

    [Theory]
    [InlineData("not-a-valid-payload")]
    [InlineData("z:00000000000000000000000000000000")]
    [InlineData("c:not-a-guid")]
    [InlineData("a:2026-13-99")]
    [InlineData("b:something")]
    public async Task HandleAsync_UnknownCallbackData_AnswersWithoutMutating(string data)
    {
        // Arrange
        AddProfile();
        AddSummaryDispatch();
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, 9);

        // Act
        await _sut.HandleAsync(Callback(Tutor, data));

        // Assert
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        Assert.Null(Assert.Single(_sender.Answered).Text);
        Assert.Empty(_sender.Edited);
    }

    [Fact]
    public async Task HandleAsync_Callback_EstablishesTheSendingTutorAsTheTenant()
    {
        // Arrange
        // The webhook is anonymous, so nothing upstream established a tenant: the update payload is
        // the only identity there is, and it must be in place before any row is touched.
        AddProfile();
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, 9);

        // Act
        await _sut.HandleAsync(Callback(Tutor, $"c:{lesson.Id:N}"));

        // Assert
        Assert.Equal(Tutor, _tenant.CurrentTutorTelegramId);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
    }

    [Fact]
    public async Task HandleAsync_UpdateFromUnreachableTutor_ReEnablesReachability()
    {
        // Arrange
        var profile = AddProfile();
        profile.MarkBotUnreachable();
        var studentId = AddStudent("Ann");
        var lesson = AddLesson(studentId, 9);

        // Act — any interaction from the tutor whose bot we'd disabled resumes notifications.
        await _sut.HandleAsync(Callback(Tutor, $"c:{lesson.Id:N}"));

        // Assert
        Assert.True(profile.BotReachable);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
