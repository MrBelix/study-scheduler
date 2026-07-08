using System.Text;
using StudyScheduler.API.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class FollowUpCallbackTests
{
    [Theory]
    [InlineData(FollowUpAction.Completed)]
    [InlineData(FollowUpAction.Paid)]
    [InlineData(FollowUpAction.Cancelled)]
    public void Lesson_reference_round_trips(FollowUpAction action)
    {
        var lessonId = Guid.NewGuid();

        var data = FollowUpCallback.Format(action, lessonId, null, null);

        Assert.True(FollowUpCallback.TryParse(data, out var parsedAction, out var slot));
        Assert.Equal(action, parsedAction);
        Assert.Equal(lessonId, slot.LessonId);
        Assert.Null(slot.SeriesId);
    }

    [Fact]
    public void Series_occurrence_reference_round_trips_and_fits_telegram_limit()
    {
        var seriesId = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 25);

        var data = FollowUpCallback.Format(FollowUpAction.Paid, null, seriesId, date);

        // Telegram rejects callback data over 64 bytes.
        Assert.True(Encoding.UTF8.GetByteCount(data) <= 64, $"'{data}' exceeds 64 bytes");
        Assert.True(FollowUpCallback.TryParse(data, out var action, out var slot));
        Assert.Equal(FollowUpAction.Paid, action);
        Assert.Equal(seriesId, slot.SeriesId);
        Assert.Equal(date, slot.OccurrenceDate);
        Assert.Null(slot.LessonId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("x:L:00000000000000000000000000000000")]
    [InlineData("p:L:not-a-guid")]
    [InlineData("p:S:00000000000000000000000000000000:99999999")]
    public void Unparsable_data_is_rejected(string? data)
    {
        Assert.False(FollowUpCallback.TryParse(data, out _, out _));
    }
}
