using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Domain.Tutors;

public class TutorProfileTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    [Fact]
    public void Create_ValidInput_SetsFields()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt);

        Assert.Equal(555, profile.TelegramUserId);
        Assert.Equal("Europe/Kyiv", profile.TimeZone.Id);
        Assert.Equal(CreatedAt, profile.CreatedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_NonPositiveTelegramId_Throws(long telegramId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TutorProfile.Create(telegramId, Kyiv, CreatedAt));
    }

    [Fact]
    public void Create_NullTimeZone_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TutorProfile.Create(555, null!, CreatedAt));
    }

    [Fact]
    public void UpdateTimeZone_Valid_Replaces()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt);

        profile.UpdateTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));

        Assert.Equal("Europe/Warsaw", profile.TimeZone.Id);
    }

    [Fact]
    public void UpdateTimeZone_Null_Throws()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt);

        Assert.Throws<ArgumentNullException>(() => profile.UpdateTimeZone(null!));
    }
}
