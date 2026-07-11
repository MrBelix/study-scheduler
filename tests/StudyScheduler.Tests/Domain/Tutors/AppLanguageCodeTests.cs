using StudyScheduler.Domain.Tutors;
using Xunit;

namespace StudyScheduler.Tests.Domain.Tutors;

public class AppLanguageCodeTests
{
    [Theory]
    [InlineData(AppLanguage.Uk, "uk")]
    [InlineData(AppLanguage.En, "en")]
    public void ToCode_ReturnsLowercaseWireCode(AppLanguage language, string expected) =>
        Assert.Equal(expected, language.ToCode());

    [Theory]
    [InlineData("uk", AppLanguage.Uk)]
    [InlineData("en", AppLanguage.En)]
    [InlineData("EN", AppLanguage.En)]   // case-insensitive
    [InlineData(" uk ", AppLanguage.Uk)] // trimmed
    public void ParseCode_SupportedCode_Succeeds(string raw, AppLanguage expected)
    {
        var result = AppLanguageCode.ParseCode(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("u")]
    [InlineData("ukr")]
    [InlineData("u1")]
    [InlineData("u-")]
    [InlineData("fr")]  // valid ISO 639-1 format but unsupported by the app
    [InlineData(null)]
    public void ParseCode_UnsupportedOrMalformed_FailsWithInvalidLanguageCode(string? raw)
    {
        var result = AppLanguageCode.ParseCode(raw);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("LanguageCode", error.Field);
        Assert.Equal("Profile.InvalidLanguageCode", error.Code);
    }
}
