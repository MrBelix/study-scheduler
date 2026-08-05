using StudyScheduler.Domain.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Domain.Notifications;

public class NotificationDispatchTests
{
    private const long Chat = 555;
    private static readonly Guid LessonId = Guid.NewGuid();
    private static readonly DateOnly LocalDate = new(2026, 8, 5);
    private static readonly DateTimeOffset SentAt = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);

    private static NotificationDispatch Reminder() =>
        NotificationDispatch.ForReminder(
            LessonId, Chat, messageId: 42, RenderedSnapshot.Empty, "hash-1", ExpiresAt, SentAt);

    [Fact]
    public void ForDay_ReminderKind_Throws()
    {
        // Arrange — a reminder targets a lesson, not a local date.
        var kind = NotificationKind.Reminder;

        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationDispatch.ForDay(
            kind, LocalDate, Chat, messageId: 42, RenderedSnapshot.Empty, "hash-1", ExpiresAt, SentAt));
    }

    [Fact]
    public void ForDay_DigestKind_TargetsTheLocalDateAndNoLesson()
    {
        // Act
        var dispatch = NotificationDispatch.ForDay(
            NotificationKind.DayAgenda, LocalDate, Chat, messageId: 42,
            RenderedSnapshot.Empty, "hash-1", ExpiresAt, SentAt);

        // Assert
        Assert.Equal(NotificationKind.DayAgenda, dispatch.Kind);
        Assert.Equal(LocalDate, dispatch.LocalDate);
        Assert.Null(dispatch.LessonId);
        Assert.True(dispatch.IsLive);
    }

    [Fact]
    public void ForReminder_NewRow_IsLiveWithGivenExpiry()
    {
        // Act
        var dispatch = Reminder();

        // Assert
        Assert.True(dispatch.IsLive);
        Assert.Equal(DispatchState.Delivered, dispatch.State);
        Assert.Equal(NotificationKind.Reminder, dispatch.Kind);
        Assert.Equal(LessonId, dispatch.LessonId);
        Assert.Null(dispatch.LocalDate);
        Assert.Equal(ExpiresAt, dispatch.ExpiresAtUtc);
        Assert.Equal(SentAt, dispatch.SentAtUtc);
        // Nothing has re-rendered it yet, so the sync stamp starts on the send itself.
        Assert.Equal(SentAt, dispatch.LastSyncedAtUtc);
    }

    [Fact]
    public void Retract_LiveRow_StopsBeingLive()
    {
        // Arrange
        var dispatch = Reminder();
        var retractedAt = SentAt.AddMinutes(10);

        // Act
        dispatch.Retract(retractedAt);

        // Assert — retracted rows do not block a new dispatch, which is what re-arms the reminder.
        Assert.False(dispatch.IsLive);
        Assert.Equal(DispatchState.Retracted, dispatch.State);
        Assert.Equal(retractedAt, dispatch.LastSyncedAtUtc);
    }

    [Fact]
    public void Retract_AlreadyRetracted_IsIdempotent()
    {
        // Arrange
        var dispatch = Reminder();
        dispatch.Retract(SentAt.AddMinutes(10));
        var secondAttempt = SentAt.AddMinutes(20);

        // Act
        dispatch.Retract(secondAttempt);

        // Assert — the same statement twice, not an error; the stamp simply moves on.
        Assert.Equal(DispatchState.Retracted, dispatch.State);
        Assert.Equal(secondAttempt, dispatch.LastSyncedAtUtc);
    }

    [Fact]
    public void Resync_RetractedRow_Throws()
    {
        // Arrange — nothing is on screen any more, so there is nothing left to keep truthful.
        var dispatch = Reminder();
        dispatch.Retract(SentAt.AddMinutes(10));

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() =>
            dispatch.Resync(RenderedSnapshot.Empty, "hash-2", SentAt.AddMinutes(20)));
    }

    [Fact]
    public void Resync_LiveRow_ReplacesSnapshotAndHash()
    {
        // Arrange
        var dispatch = Reminder();
        var snapshot = RenderedSnapshot.Empty with { Lang = "en" };
        var syncedAt = SentAt.AddMinutes(10);

        // Act
        dispatch.Resync(snapshot, "hash-2", syncedAt);

        // Assert
        Assert.Same(snapshot, dispatch.Snapshot);
        Assert.Equal("hash-2", dispatch.ContentHash);
        Assert.Equal(syncedAt, dispatch.LastSyncedAtUtc);
        // Re-rendering is not retiring: the row is still the live message for its lesson.
        Assert.True(dispatch.IsLive);
    }
}
