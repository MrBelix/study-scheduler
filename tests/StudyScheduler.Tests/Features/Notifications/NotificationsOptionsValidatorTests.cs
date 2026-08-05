using Microsoft.Extensions.Options;
using StudyScheduler.API.Features.Notifications;
using Xunit;

namespace StudyScheduler.Tests.Features.Notifications;

public class NotificationsOptionsValidatorTests
{
    private readonly NotificationsOptionsValidator _sut = new();

    private ValidateOptionsResult Validate(NotificationsOptions options) => _sut.Validate(null, options);

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        // Arrange — the defaults also disable both the webhook and the deep links, which is valid.
        var options = new NotificationsOptions();

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_PollIntervalZero_Fails()
    {
        // Arrange
        var options = new NotificationsOptions { PollIntervalMinutes = 0 };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_PollIntervalAboveMinRemind_Fails()
    {
        // Arrange
        // MinRemindMinutes is 5; anything above it can skip a reminder between ticks.
        var options = new NotificationsOptions { PollIntervalMinutes = 6 };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_NegativeSummaryGrace_Fails()
    {
        // Arrange — a negative grace would send the summary before the day's last lesson ends.
        var options = new NotificationsOptions { SummaryGraceMinutes = -1 };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_ZeroSummaryGrace_Succeeds()
    {
        // Arrange — sending the moment the day's last lesson ends is a legitimate choice.
        var options = new NotificationsOptions { SummaryGraceMinutes = 0 };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MaxTelegramCallsPerTickBelowOne_Fails()
    {
        // Arrange — a tick allowed no calls at all would never drain the queue.
        var options = new NotificationsOptions { MaxTelegramCallsPerTick = 0 };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_AbsoluteHttpsMiniAppUrl_Succeeds()
    {
        // Arrange
        var options = new NotificationsOptions { MiniAppUrl = "https://app.example.org/" };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Theory]
    // Not absolute, not https, and one that already carries a query string — "?startapp=" is
    // appended to this URL, so it cannot bring one of its own.
    [InlineData("/app")]
    [InlineData("http://app.example.org/")]
    [InlineData("https://app.example.org/?ref=bot")]
    public void Validate_UnusableMiniAppUrl_Fails(string miniAppUrl)
    {
        // Arrange
        var options = new NotificationsOptions { MiniAppUrl = miniAppUrl };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("Notifications:MiniAppUrl", result.FailureMessage);
    }

    [Fact]
    public void Validate_EmptyMiniAppUrl_Succeeds()
    {
        // Arrange — empty means "emit no url buttons", which is the dev/test default.
        var options = new NotificationsOptions { MiniAppUrl = "" };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WebhookUrlSetWithoutSecret_Fails()
    {
        // Arrange
        var options = new NotificationsOptions { WebhookUrl = "https://example.com/telegram/webhook" };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_BothWebhookFieldsEmpty_Succeeds()
    {
        // Arrange — the default disables the webhook, which is valid.
        var options = new NotificationsOptions();

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Succeeded);
    }
}
