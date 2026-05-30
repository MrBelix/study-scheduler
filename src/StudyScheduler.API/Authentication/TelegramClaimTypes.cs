namespace StudyScheduler.API.Authentication;

/// <summary>Custom claim types for data carried from Telegram init data.</summary>
public static class TelegramClaimTypes
{
    public const string Username = "tg:username";
    public const string LanguageCode = "tg:language_code";
    public const string IsPremium = "tg:is_premium";
}
