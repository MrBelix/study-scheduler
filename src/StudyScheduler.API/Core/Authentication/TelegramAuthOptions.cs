using Microsoft.AspNetCore.Authentication;

namespace StudyScheduler.API.Core.Authentication;

/// <summary>
/// Options for the Telegram Mini App authentication scheme.
/// Bound from the "TelegramAuth" configuration section.
/// </summary>
public sealed class TelegramAuthOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "TelegramWebApp";

    /// <summary>Bot token issued by BotFather. Keep out of appsettings — use secrets/env.</summary>
    public string BotToken { get; set; } = string.Empty;
}
