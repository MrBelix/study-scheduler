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
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

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
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

        profile.UpdateTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));

        Assert.Equal("Europe/Warsaw", profile.TimeZone.Id);
    }

    [Fact]
    public void UpdateTimeZone_Null_Throws()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

        Assert.Throws<ArgumentNullException>(() => profile.UpdateTimeZone(null!));
    }

    [Fact]
    public void Create_WithoutLanguage_LeavesItNull()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

        Assert.Null(profile.LanguageCode);
    }

    [Fact]
    public void Create_WithLanguage_SetsIt()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt, languageCode: AppLanguage.Uk).Value;

        Assert.Equal(AppLanguage.Uk, profile.LanguageCode);
    }

    [Fact]
    public void UpdateLanguage_Replaces()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt, languageCode: AppLanguage.Uk).Value;

        profile.UpdateLanguage(AppLanguage.En);

        Assert.Equal(AppLanguage.En, profile.LanguageCode);
    }

    [Fact]
    public void Create_NotificationsOnByDefault()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

        Assert.Equal(TutorProfile.DefaultRemindMinutes, profile.RemindMinutes);
        Assert.True(profile.NotifyAfterLesson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TutorProfile.MinRemindMinutes)]
    [InlineData(60)]
    [InlineData(TutorProfile.MaxRemindMinutes)]
    public void UpdateRemindMinutes_ValidOrOff_Replaces(int? minutes)
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

        var result = profile.UpdateRemindMinutes(minutes);

        Assert.True(result.IsSuccess);
        Assert.Equal(minutes, profile.RemindMinutes);
    }

    [Theory]
    [InlineData(TutorProfile.MinRemindMinutes - 1)]
    [InlineData(TutorProfile.MaxRemindMinutes + 1)]
    [InlineData(0)]
    public void UpdateRemindMinutes_OutOfRange_FailsWithoutMutating(int minutes)
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

        var result = profile.UpdateRemindMinutes(minutes);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("RemindMinutes", error.Field);
        Assert.Equal("Profile.RemindMinutesOutOfRange", error.Code);
        Assert.Equal(TutorProfile.DefaultRemindMinutes, profile.RemindMinutes);
    }

    [Fact]
    public void UpdateNotifyAfterLesson_Replaces()
    {
        var profile = TutorProfile.Create(555, Kyiv, CreatedAt).Value;

        profile.UpdateNotifyAfterLesson(false);

        Assert.False(profile.NotifyAfterLesson);
    }
}
