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

        // Every advertised id must pass the exact check PUT /profile applies.
        Assert.All(ids, id => Assert.True(
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out _), $"'{id}' is not resolvable"));

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

    private async Task<ProfileDto> PutProfile(string initData, object body)
    {
        var response = await app.Api.PutAs(initData, "/profile", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProfileDto>())!;
    }

    private sealed record ProfileDto(string TimeZoneId, string? LanguageCode, DateTimeOffset CreatedAtUtc);
}
