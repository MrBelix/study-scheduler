using Microsoft.AspNetCore.Authentication;

namespace StudyScheduler.API.Authentication;

/// <summary>
/// Options for the Telegram Mini App authentication scheme.
/// Bound from the "TelegramAuth" configuration section.
/// </summary>
public sealed class TelegramAuthOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "TelegramWebApp";

    /// <summary>Bot token issued by BotFather. Keep out of appsettings — use secrets/env.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Maximum age of <c>auth_date</c> before init data is considered expired.
    /// initData is fixed at app launch and does not refresh, so keep this generous.
    /// </summary>
    public TimeSpan MaxAuthAge { get; set; } = TimeSpan.FromHours(24);
}
