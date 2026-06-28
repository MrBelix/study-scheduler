using System.Security.Claims;

namespace StudyScheduler.API.Core.Authentication;

/// <summary>
/// Single access point for the Telegram identity carried in the authenticated principal,
/// so claim keys don't leak into every handler.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static long GetTelegramId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (long.TryParse(raw, out var id))
            return id;

        throw new InvalidOperationException("Authenticated principal has no Telegram id claim.");
    }
}
