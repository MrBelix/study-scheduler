using StudyScheduler.API.Features.Notifications;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.Tests.Features.Notifications;

// Hand-rolled fakes for the notification pipeline. Each implements only what the tests
// exercise; everything else throws so an unexpected call fails loudly instead of silently.

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public int DiscardCount { get; private set; }

    /// <summary>Outcomes of successive saves: a queued exception is thrown, an empty queue means success.</summary>
    public Queue<Exception> SaveFailures { get; } = new();

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        if (SaveFailures.Count > 0)
            throw SaveFailures.Dequeue();
        return Task.CompletedTask;
    }

    public void DiscardChanges() => DiscardCount++;
}

internal sealed class FakeTelegramBotClient : ITelegramBotClient
{
    /// <summary>Result every SendMessageAsync reports; flip to false to simulate delivery failure.</summary>
    public bool SendResult { get; set; } = true;

    public List<(long ChatId, string Text, IReadOnlyList<BotButton>? Buttons)> SentMessages { get; } = [];

    public List<(string CallbackQueryId, string Text)> AnsweredCallbacks { get; } = [];

    public List<(long ChatId, long MessageId, string Text)> EditedMessages { get; } = [];

    public Task<bool> SendMessageAsync(
        long chatId, string text, IReadOnlyList<BotButton>? buttons = null, CancellationToken ct = default)
    {
        SentMessages.Add((chatId, text, buttons));
        return Task.FromResult(SendResult);
    }

    public Task<bool> AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken ct = default)
    {
        AnsweredCallbacks.Add((callbackQueryId, text));
        return Task.FromResult(true);
    }

    public Task<bool> EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken ct = default)
    {
        EditedMessages.Add((chatId, messageId, text));
        return Task.FromResult(true);
    }

    public Task<bool> SetWebhookAsync(string url, string secretToken, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeTutorProfileRepository : ITutorProfileRepository
{
    public List<TutorProfile> Profiles { get; } = [];

    public Task<TutorProfile?> GetAsync(long telegramUserId, CancellationToken ct = default) =>
        Task.FromResult(Profiles.FirstOrDefault(p => p.TelegramUserId == telegramUserId));

    public Task<List<TutorProfile>> GetWithNotificationsEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult(Profiles.Where(p => p.RemindMinutes is not null || p.NotifyAfterLesson).ToList());

    public void Add(TutorProfile profile) => throw new NotSupportedException();

    public void Update(TutorProfile profile) => throw new NotSupportedException();
}

internal sealed class FakeStudentRepository : IStudentRepository
{
    public List<Student> Students { get; } = [];

    public Task<Student?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Students.FirstOrDefault(s => s.Id == id && s.TutorTelegramId == tutorTelegramId));

    public Task<List<Student>> GetByIdsAsync(
        long tutorTelegramId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult(Students.Where(s => s.TutorTelegramId == tutorTelegramId && ids.Contains(s.Id)).ToList());

    public Task<List<Student>> GetAllByTutorIdAsync(long tutorTelegramId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public void Add(Student student) => throw new NotSupportedException();

    public void Update(Student student) => throw new NotSupportedException();
}

internal sealed class FakeLessonNotificationRepository : ILessonNotificationRepository
{
    /// <summary>Slot keys pretending to have already received a notification (any kind).</summary>
    public HashSet<string> AlreadySent { get; } = [];

    public List<LessonNotification> Added { get; } = [];

    /// <summary>When set, lookups for this tutor throw — simulates one tutor's pass failing.</summary>
    public long? ThrowForTutor { get; set; }

    public Task<HashSet<string>> GetSentSlotKeysAsync(
        long tutorTelegramId,
        LessonNotificationKind kind,
        IReadOnlyCollection<string> slotKeys,
        CancellationToken ct = default)
    {
        if (tutorTelegramId == ThrowForTutor)
            throw new InvalidOperationException("Simulated dedup-log failure.");
        return Task.FromResult(AlreadySent.Where(slotKeys.Contains).ToHashSet());
    }

    public void Add(LessonNotification notification) => Added.Add(notification);
}

internal sealed class FakeLessonRepository : ILessonRepository
{
    public List<Lesson> Lessons { get; } = [];

    /// <summary>Successive results of <see cref="GetBySeriesOccurrenceAsync"/>; empty queue = null.</summary>
    public Queue<Lesson?> SeriesOccurrenceResults { get; } = new();

    public List<Lesson> Added { get; } = [];

    public List<Lesson> Updated { get; } = [];

    public Task<Lesson?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Lessons.FirstOrDefault(l => l.Id == id && l.TutorTelegramId == tutorTelegramId));

    /// <summary>Every window <see cref="GetOverlappingAsync"/> was asked about, in call order.</summary>
    public List<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> OverlapQueries { get; } = [];

    public Task<List<Lesson>> GetByTutorInRangeAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default) =>
        Task.FromResult(Lessons
            .Where(l => l.TutorTelegramId == tutorTelegramId && l.StartUtc < toUtc && l.EndUtc > fromUtc
                && (studentId is null || l.StudentId == studentId))
            .ToList());

    // Mirrors EfLessonRepository: cancelled lessons never conflict, overlap is strict.
    public Task<List<Lesson>> GetOverlappingAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        CancellationToken ct = default)
    {
        OverlapQueries.Add((startUtc, endUtc));
        return Task.FromResult(Lessons
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc < endUtc
                && l.EndUtc > startUtc
                && (excludeLessonId is null || l.Id != excludeLessonId))
            .ToList());
    }

    public Task<List<Lesson>> GetFromDateAsync(
        long tutorTelegramId, DateTimeOffset fromUtc, CancellationToken ct = default) =>
        Task.FromResult(Lessons
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc >= fromUtc)
            .ToList());

    public Task<List<(Guid SeriesId, DateOnly OccurrenceDate)>> GetOccurrenceDatesForSeriesAsync(
        IReadOnlyCollection<Guid> seriesIds,
        DateOnly fromLocal,
        DateOnly toLocal,
        CancellationToken ct = default) =>
        Task.FromResult(Lessons
            .Where(l => l.SeriesId is { } sid && seriesIds.Contains(sid)
                && l.OccurrenceDate >= fromLocal && l.OccurrenceDate <= toLocal)
            .Select(l => (l.SeriesId!.Value, l.OccurrenceDate!.Value))
            .ToList());

    public Task<Lesson?> GetBySeriesOccurrenceAsync(
        Guid seriesId, DateOnly occurrenceDate, CancellationToken ct = default) =>
        Task.FromResult(SeriesOccurrenceResults.Count > 0 ? SeriesOccurrenceResults.Dequeue() : null);

    public void Add(Lesson lesson) => Added.Add(lesson);

    public void Update(Lesson lesson) => Updated.Add(lesson);
}

internal sealed class FakeLessonSeriesRepository : ILessonSeriesRepository
{
    public List<LessonSeries> Series { get; } = [];

    public Task<LessonSeries?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Series.FirstOrDefault(s => s.Id == id && s.TutorTelegramId == tutorTelegramId));

    public Task<List<LessonSeries>> GetActiveByTutorAsync(long tutorTelegramId, CancellationToken ct = default) =>
        Task.FromResult(Series.Where(s => s.TutorTelegramId == tutorTelegramId && s.IsActive).ToList());

    public Task<List<LessonSeries>> GetActiveByStudentAsync(
        long tutorTelegramId, Guid studentId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<List<LessonSeries>> GetAllByTutorAsync(long tutorTelegramId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<List<LessonSeries>> GetActiveByTimeZoneAsync(
        long tutorTelegramId, TimeZoneInfo timeZone, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public void Add(LessonSeries series) => throw new NotSupportedException();

    public void Update(LessonSeries series) => throw new NotSupportedException();
}
