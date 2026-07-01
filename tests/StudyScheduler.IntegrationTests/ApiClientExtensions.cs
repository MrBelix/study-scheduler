using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// Sends requests as a given Telegram user by attaching the <c>Authorization: tma &lt;initData&gt;</c>
/// header, so tests read like "tutor A does X" without HTTP plumbing.
/// </summary>
internal static class ApiClientExtensions
{
    public static Task<HttpResponseMessage> GetAs(this HttpClient client, string initData, string url) =>
        client.SendAsync(Authed(HttpMethod.Get, url, initData));

    public static Task<HttpResponseMessage> PostAs(this HttpClient client, string initData, string url, object body) =>
        client.SendAsync(Authed(HttpMethod.Post, url, initData, JsonContent.Create(body)));

    private static HttpRequestMessage Authed(HttpMethod method, string url, string initData, HttpContent? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body };
        request.Headers.TryAddWithoutValidation("Authorization", $"tma {initData}");
        return request;
    }
}
