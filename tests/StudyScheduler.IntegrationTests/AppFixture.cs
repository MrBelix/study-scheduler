using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// Boots the whole Aspire app (SQL Server container + API) once and shares it across the test
/// collection — starting the container is expensive, so we pay it a single time.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private DistributedApplication? _app;

    public HttpClient Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.StudyScheduler_AppHost>();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            // Suppress the piped stdout of the SQL container and the API (EF commands, HTTP logs).
            logging.AddFilter("StudyScheduler.AppHost.Resources", LogLevel.Warning);
            logging.AddFilter("Aspire.Hosting", LogLevel.Warning);
        });

        _app = await builder.BuildAsync();
        await _app.StartAsync();

        await _app.ResourceNotifications
            .WaitForResourceHealthyAsync("api")
            .WaitAsync(TimeSpan.FromMinutes(8));

        // Talk to the API's HTTPS endpoint but skip cert validation: the ASP.NET dev cert isn't
        // trusted on CI (Linux) runners, which otherwise fails the TLS handshake. Fine for tests.
        Api = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = _app.GetEndpoint("api", "https"),
        };
    }

    public async Task DisposeAsync()
    {
        Api?.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
    }
}

/// <summary>Shares a single <see cref="AppFixture"/> across every test class in the collection.</summary>
[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFixture>;
