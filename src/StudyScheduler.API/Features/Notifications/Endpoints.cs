using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>HTTP handler for the Telegram webhook. Wired to a route in <see cref="NotificationsModule"/>.</summary>
internal static class Endpoints
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>
    /// Anonymous webhook Telegram POSTs updates to. Guards on the shared secret (404 — never leak that
    /// the endpoint exists), deserializes the body with Telegram.Bot's own serializer options (the
    /// app's camelCase defaults won't bind an <see cref="Telegram.Bot.Types.Update"/> correctly), then
    /// hands off to the handler. Always answers 200 so Telegram doesn't retry-storm.
    /// </summary>
    public static async Task<IResult> Webhook(
        HttpContext http,
        IOptions<NotificationsOptions> options,
        TelegramWebhookHandler handler,
        CancellationToken ct)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
            return Results.NotFound(); // webhook disabled

        if (!SecretMatches(http.Request.Headers[SecretHeader].ToString(), secret))
            return Results.NotFound(); // wrong/absent secret — don't leak existence

        Telegram.Bot.Types.Update? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<Telegram.Bot.Types.Update>(
                http.Request.Body, JsonBotAPI.Options, ct);
        }
        catch (JsonException)
        {
            // A malformed body is not worth a retry — ack and drop.
            return Results.Ok();
        }

        if (update is null)
            return Results.Ok();

        await handler.HandleAsync(update, ct);
        return Results.Ok();
    }

    /// <summary>
    /// Compares the presented secret to the configured one without leaking WHERE they diverge: this
    /// gate is the whole authorization of the webhook — everything past it acts as the tenant the
    /// payload names — so an ordinary <c>!=</c>, which stops at the first differing character, would
    /// hand an attacker a timing oracle to guess the secret byte by byte.
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> takes the same time for every equal-length
    /// candidate and simply returns false on a length mismatch (a length is not worth protecting).
    /// The same primitive guards the init-data signature in <c>TelegramInitDataValidator</c>.
    /// </summary>
    private static bool SecretMatches(string presented, string secret) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(secret));
}
