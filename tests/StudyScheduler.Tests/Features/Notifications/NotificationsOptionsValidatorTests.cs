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
        // Arrange
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
        var options = new NotificationsOptions { PollIntervalMinutes = 6, FollowUpLookbackMinutes = 60 };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_LookbackBelowPollInterval_Fails()
    {
        // Arrange
        var options = new NotificationsOptions { PollIntervalMinutes = 5, FollowUpLookbackMinutes = 4 };

        // Act
        var result = Validate(options);

        // Assert
        Assert.True(result.Failed);
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
