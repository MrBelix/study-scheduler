using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using StudyScheduler.API.Core.ErrorHandling;
using Xunit;

namespace StudyScheduler.Tests.Core.ErrorHandling;

/// <summary>
/// The status the global handler puts on the wire: an opaque 500 for genuine faults, but the
/// framework's own status for a malformed request. The latter is what a client sees when minimal
/// APIs fail to bind a route value in Development, where RouteHandlerOptions.ThrowOnBadRequest turns
/// that 400 into a BadHttpRequestException travelling through this handler.
/// </summary>
public class GlobalExceptionHandlerTests
{
    private static readonly IProblemDetailsService ProblemDetails =
        new ServiceCollection()
            .AddOptions()
            .AddProblemDetails()
            .BuildServiceProvider()
            .GetRequiredService<IProblemDetailsService>();

    [Fact]
    public async Task TryHandleAsync_UnexpectedException_AnswersInternalServerError()
    {
        // Arrange
        var context = NewContext();

        // Act
        var handled = await NewHandler().TryHandleAsync(context, new InvalidOperationException("boom"), default);

        // Assert
        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_BadHttpRequestException_AnswersItsOwnStatusCode()
    {
        // Arrange
        var context = NewContext();
        var exception = new BadHttpRequestException("Malformed.", StatusCodes.Status400BadRequest);

        // Act
        var handled = await NewHandler().TryHandleAsync(context, exception, default);

        // Assert
        // The framework already decided this is the caller's fault; reporting 500 would both lie to
        // the client and hide a plain 400 behind an alert-worthy status.
        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_OversizedBodyException_KeepsThePayloadTooLargeStatus()
    {
        // Arrange
        // Kestrel raises this one in every environment, so it is not a Development-only concern.
        var context = NewContext();
        var exception = new BadHttpRequestException("Too large.", StatusCodes.Status413PayloadTooLarge);

        // Act
        await NewHandler().TryHandleAsync(context, exception, default);

        // Assert
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_MalformedRouteValueThrownByRouteBinding_AnswersBadRequest()
    {
        // Arrange
        // The real Development pipeline end to end: ThrowOnBadRequest is on, so binding an unparsable
        // route value throws instead of writing 400 itself, and the throw lands here.
        var invoked = 0;
        var handler = RequestDelegateFactory.Create(
            (Guid id) => { invoked++; return id.ToString(); },
            new RequestDelegateFactoryOptions { ThrowOnBadRequest = true });
        var context = NewContext();
        context.Request.RouteValues["id"] = "nonsense";

        // Act
        var thrown = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => handler.RequestDelegate(context));
        await NewHandler().TryHandleAsync(context, thrown, default);

        // Assert
        Assert.Equal(0, invoked);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static GlobalExceptionHandler NewHandler() =>
        new(ProblemDetails, new Env(), NullLogger<GlobalExceptionHandler>.Instance);

    private static DefaultHttpContext NewContext() =>
        new() { Response = { Body = new MemoryStream() } };

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "StudyScheduler.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
