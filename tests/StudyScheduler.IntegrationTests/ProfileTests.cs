using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>End-to-end tests for the Profile feature over the real stack.</summary>
[Collection(nameof(AppCollection))]
public class ProfileTests(AppFixture app)
{
    [Fact]
    public async Task Timezones_returns_iana_ids_that_profile_put_accepts()
    {
        var tutor = TelegramInitData.ForUser(3201, "Alice");

        var response = await app.Api.GetAs(tutor, "/profile/timezones");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await response.Content.ReadFromJsonAsync<List<string>>())!;

        Assert.NotEmpty(ids);
        // Linux (prod) tzdb carries the modern name; ICU's Windows→IANA golden mapping (local
        // dev) still uses the legacy one. Either spelling resolves to the same zone.
        Assert.Contains(ids, id => id is "Europe/Kyiv" or "Europe/Kiev");
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // No Windows ids may leak through, whatever OS the host runs on: Windows ids all contain
        // spaces ("FLE Standard Time"), IANA ids never do.
        Assert.All(ids, id => Assert.DoesNotContain(' ', id));

        // Every advertised id must be resolvable the way PUT /profile resolves it: directly, or
        // via its rename twin (the list adds the spelling the host tzdata may lack).
        Assert.All(ids, id => Assert.True(
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)
            || (RenameTwins.TryGetValue(id, out var twin) && TimeZoneInfo.TryFindSystemTimeZoneById(twin, out _)),
            $"'{id}' is not resolvable"));

        // And the list round-trips end-to-end into PUT /profile.
        var put = await app.Api.PutAs(tutor, "/profile", new { timeZoneId = ids[0] });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task Timezones_requires_authentication()
    {
        var response = await app.Api.GetAsync("/profile/timezones");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_upserts_language_without_clobbering_it_on_timezone_saves()
    {
        var tutor = TelegramInitData.ForUser(3202, "Alice");

        // First save creates the profile; language not chosen yet → null.
        var created = await PutProfile(tutor, new { timeZoneId = "Europe/Kyiv" });
        Assert.Null(created.LanguageCode);

        // Language save (client sends the current zone alongside). Mixed case normalizes.
        var withLanguage = await PutProfile(tutor, new { timeZoneId = "Europe/Kyiv", languageCode = "EN" });
        Assert.Equal("en", withLanguage.LanguageCode);

        // A timezone-only save (languageCode null) must not clear the stored language…
        var zoneOnly = await PutProfile(tutor, new { timeZoneId = "Europe/Warsaw" });
        Assert.Equal("Europe/Warsaw", zoneOnly.TimeZoneId);
        Assert.Equal("en", zoneOnly.LanguageCode);

        // …and the language survives a fresh GET, not just the PUT echo.
        var get = await app.Api.GetAs(tutor, "/profile");
        var fetched = (await get.Content.ReadFromJsonAsync<ProfileDto>())!;
        Assert.Equal("en", fetched.LanguageCode);
        Assert.Equal("Europe/Warsaw", fetched.TimeZoneId);
    }

    [Fact]
    public async Task Notification_settings_default_on_and_survive_partial_saves()
    {
        var tutor = TelegramInitData.ForUser(3205, "Alice");

        // First save creates the profile with notifications on by default.
        var created = await PutProfile(tutor, new { timeZoneId = "Europe/Kyiv" });
        Assert.Equal(30, created.RemindMinutes);
        Assert.True(created.NotifyAfterLesson);

        // Explicit settings save: 0 turns reminders off, follow-up off too.
        var disabled = await PutProfile(tutor, new { timeZoneId = "Europe/Kyiv", remindMinutes = 0, notifyAfterLesson = false });
        Assert.Null(disabled.RemindMinutes);
        Assert.False(disabled.NotifyAfterLesson);

        // A timezone-only save must not touch the notification settings…
        var zoneOnly = await PutProfile(tutor, new { timeZoneId = "Europe/Warsaw" });
        Assert.Null(zoneOnly.RemindMinutes);
        Assert.False(zoneOnly.NotifyAfterLesson);

        // …and an out-of-range lead time is rejected.
        var invalid = await app.Api.PutAs(tutor, "/profile", new { timeZoneId = "Europe/Warsaw", remindMinutes = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Timezones_advertises_both_spellings_of_renamed_zones_and_put_accepts_either()
    {
        var tutor = TelegramInitData.ForUser(3204, "Alice");

        // Devices may detect either the modern or the pre-2022 spelling; both must be offered…
        var ids = (await (await app.Api.GetAs(tutor, "/profile/timezones")).Content
            .ReadFromJsonAsync<List<string>>())!;
        Assert.Contains("Europe/Kyiv", ids);
        Assert.Contains("Europe/Kiev", ids);

        // …and both must save, whichever spelling the host tzdata canonicalizes to.
        var modern = await PutProfile(tutor, new { timeZoneId = "Europe/Kyiv" });
        Assert.Contains(modern.TimeZoneId, new[] { "Europe/Kyiv", "Europe/Kiev" });
        var legacy = await PutProfile(tutor, new { timeZoneId = "Europe/Kiev" });
        Assert.Contains(legacy.TimeZoneId, new[] { "Europe/Kyiv", "Europe/Kiev" });
    }

    [Theory]
    [InlineData("ukr")]
    [InlineData("u")]
    [InlineData("u1")]
    [InlineData("")]
    public async Task Put_with_invalid_language_code_returns_validation_problem(string languageCode)
    {
        var tutor = TelegramInitData.ForUser(3203, "Alice");

        var response = await app.Api.PutAs(tutor, "/profile", new { timeZoneId = "Europe/Kyiv", languageCode });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Mirror of the API's rename-twin fallback (see IanaTimeZone in the API project).</summary>
    private static readonly Dictionary<string, string> RenameTwins = new()
    {
        ["Europe/Kyiv"] = "Europe/Kiev",
        ["Europe/Kiev"] = "Europe/Kyiv",
        ["America/Nuuk"] = "America/Godthab",
        ["America/Godthab"] = "America/Nuuk",
        ["Asia/Yangon"] = "Asia/Rangoon",
        ["Asia/Rangoon"] = "Asia/Yangon",
        ["Asia/Kolkata"] = "Asia/Calcutta",
        ["Asia/Calcutta"] = "Asia/Kolkata",
        ["Asia/Ho_Chi_Minh"] = "Asia/Saigon",
        ["Asia/Saigon"] = "Asia/Ho_Chi_Minh",
    };

    private async Task<ProfileDto> PutProfile(string initData, object body)
    {
        var response = await app.Api.PutAs(initData, "/profile", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProfileDto>())!;
    }

    private sealed record ProfileDto(
        string TimeZoneId,
        string? LanguageCode,
        int? RemindMinutes,
        bool NotifyAfterLesson,
        DateTimeOffset CreatedAtUtc);
}
