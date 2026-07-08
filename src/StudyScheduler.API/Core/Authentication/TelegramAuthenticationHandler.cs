using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace StudyScheduler.API.Core.Authentication;

/// <summary>
/// Authenticates each request by validating the Telegram init data passed in the
/// <c>Authorization: tma &lt;initData&gt;</c> header. No server-side session is created —
/// init data is re-validated on every request.
/// </summary>
public sealed class TelegramAuthenticationHandler(
    IOptionsMonitor<TelegramAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TelegramInitDataValidator validator)
    : AuthenticationHandler<TelegramAuthOptions>(options, logger, encoder)
{
    private const string HeaderPrefix = "tma ";

    // The handler is resolved per request, so stashing the reason here lets
    // HandleChallengeAsync report why authentication failed.
    private TelegramAuthError _error = TelegramAuthError.None;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header) ||
            header.ToString() is not { } raw ||
            !raw.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var initData = raw[HeaderPrefix.Length..].Trim();
        _error = validator.Validate(initData, out var user);

        if (_error != TelegramAuthError.None || user is null)
            return Task.FromResult(AuthenticateResult.Fail($"Telegram auth failed: {_error}"));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        if (!string.IsNullOrEmpty(user.Username))
            claims.Add(new Claim(TelegramClaimTypes.Username, user.Username));
        if (!string.IsNullOrEmpty(user.FirstName))
            claims.Add(new Claim(ClaimTypes.GivenName, user.FirstName));
        if (!string.IsNullOrEmpty(user.LastName))
            claims.Add(new Claim(ClaimTypes.Surname, user.LastName));
        if (!string.IsNullOrEmpty(user.LanguageCode))
            claims.Add(new Claim(TelegramClaimTypes.LanguageCode, user.LanguageCode));
        if (user.IsPremium)
            claims.Add(new Claim(TelegramClaimTypes.IsPremium, "true"));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var (code, title) = _error switch
        {
            TelegramAuthError.Expired => ("expired", "Telegram init data has expired."),
            TelegramAuthError.InvalidSignature => ("invalid_signature", "Telegram init data signature is invalid."),
            TelegramAuthError.MissingData => ("missing_data", "Telegram init data is missing or malformed."),
            _ => ("unauthorized", "Authentication is required."),
        };

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{TelegramAuthOptions.Scheme} error=\"{code}\"";

        // RFC 7807 body, aligned with every other API failure. The top-level "error" member is
        // a ProblemDetails extension the frontend contract depends on: ApiError.fromResponse
        // reads `body.error` as the machine code and flags "expired" as the auth-expired
        // terminal state — it must survive any reshaping of this payload.
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = title,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        };
        problem.Extensions["error"] = code;
        await Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}
