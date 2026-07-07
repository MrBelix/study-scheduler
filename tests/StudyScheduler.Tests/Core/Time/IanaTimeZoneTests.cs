using StudyScheduler.API.Core.Time;
using Xunit;

namespace StudyScheduler.Tests.Core.Time;

public class IanaTimeZoneTests
{
    [Theory]
    [InlineData("Europe/Kyiv")]
    [InlineData("Europe/Kiev")] // pre-2022 spelling devices may still send
    [InlineData(" Europe/Kyiv ")] // ids arrive from clients untrimmed
    public void TryResolve_EitherRenameSpelling_ResolvesToTheSameZone(string id)
    {
        Assert.True(IanaTimeZone.TryResolve(id, out var timeZone));

        // Both spellings must land on the Kyiv rules, whichever id the host canonicalizes to.
        var july = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(TimeSpan.FromHours(3), timeZone.GetUtcOffset(july));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Europe/Atlantis")]
    [InlineData("FLE Standard Time!")]
    public void TryResolve_UnknownOrEmptyId_Fails(string? id)
    {
        Assert.False(IanaTimeZone.TryResolve(id, out var timeZone));
        Assert.Null(timeZone);
    }

    [Fact]
    public void WithRenameTwins_AddsTheMissingSpellingOnly()
    {
        var ids = new[] { "Europe/Kiev", "Europe/Warsaw" };

        var augmented = IanaTimeZone.WithRenameTwins(ids).ToList();

        Assert.Contains("Europe/Kyiv", augmented);
        Assert.Contains("Europe/Kiev", augmented);
        Assert.Contains("Europe/Warsaw", augmented);
        // No twin pair touches Warsaw; nothing unrelated is invented.
        Assert.Equal(3, augmented.Count);
    }

    [Fact]
    public void WithRenameTwins_BothSpellingsPresent_AddsNothing()
    {
        var ids = new[] { "Europe/Kyiv", "Europe/Kiev" };

        Assert.Equal(2, IanaTimeZone.WithRenameTwins(ids).Count());
    }
}
