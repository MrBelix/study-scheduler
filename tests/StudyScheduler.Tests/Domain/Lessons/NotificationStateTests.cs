using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class NotificationStateTests
{
    private static readonly DateTimeOffset SentAt = new(2026, 7, 6, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void None_HasBothTimestampsNullAndNothingSent()
    {
        var state = NotificationState.None;

        Assert.Null(state.ReminderSentAtUtc);
        Assert.Null(state.FollowUpSentAtUtc);
        Assert.False(state.IsReminderSent);
        Assert.False(state.IsFollowUpSent);
    }

    [Fact]
    public void WithReminderSent_SetsReminderLeavesFollowUpAndReturnsNewInstance()
    {
        var original = NotificationState.None;

        var updated = original.WithReminderSent(SentAt);

        Assert.Equal(SentAt, updated.ReminderSentAtUtc);
        Assert.True(updated.IsReminderSent);
        Assert.Null(updated.FollowUpSentAtUtc);
        Assert.False(updated.IsFollowUpSent);

        // Immutability: the original is untouched and a new instance is returned.
        Assert.NotSame(original, updated);
        Assert.Null(original.ReminderSentAtUtc);
        Assert.False(original.IsReminderSent);
    }

    [Fact]
    public void WithFollowUpSent_SetsFollowUpLeavesReminderAndReturnsNewInstance()
    {
        var original = NotificationState.None;

        var updated = original.WithFollowUpSent(SentAt);

        Assert.Equal(SentAt, updated.FollowUpSentAtUtc);
        Assert.True(updated.IsFollowUpSent);
        Assert.Null(updated.ReminderSentAtUtc);
        Assert.False(updated.IsReminderSent);

        // Immutability: the original is untouched and a new instance is returned.
        Assert.NotSame(original, updated);
        Assert.Null(original.FollowUpSentAtUtc);
        Assert.False(original.IsFollowUpSent);
    }
}
