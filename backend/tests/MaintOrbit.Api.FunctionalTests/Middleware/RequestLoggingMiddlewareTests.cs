using MaintOrbit.Shared.Constants;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Api.FunctionalTests.Middleware;

/// <summary>
/// Covers the per-request log entry.
/// </summary>
public sealed class RequestLoggingMiddlewareTests
{
    private const int RequestCompletedEventId = 1000;

    private static IEnumerable<RecordedLogEntry> RequestEntries(RecordingLoggerProvider recorder) =>
        recorder.Entries.Where(entry => entry.EventId.Id == RequestCompletedEventId);

    [Fact]
    public async Task SuccessfulRequest_IsLoggedOnce_AtInformation()
    {
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri(PipelineTestHost.SucceedingPath, UriKind.Relative));

        var entry = Assert.Single(RequestEntries(recorder));
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public async Task RequestLog_CarriesMethodPathStatusAndDuration()
    {
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri(PipelineTestHost.SucceedingPath, UriKind.Relative));

        var entry = Assert.Single(RequestEntries(recorder));
        Assert.Contains("GET", entry.Message, StringComparison.Ordinal);
        Assert.Contains(PipelineTestHost.SucceedingPath, entry.Message, StringComparison.Ordinal);
        Assert.Contains("200", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ms", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestLog_CarriesTheCorrelationIdentifier()
    {
        // LG-4. The identifier is not written into the message — it arrives from the ambient
        // scope, which is what makes it present on entries this middleware never wrote.
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri(PipelineTestHost.SucceedingPath, UriKind.Relative));
        var expected = Assert.Single(response.Headers.GetValues(CorrelationHeaderNames.CorrelationId));

        Assert.Equal(expected, Assert.Single(RequestEntries(recorder)).CorrelationId);
    }

    [Fact]
    public async Task FailedRequest_IsLoggedOnce_AtError_WithTheStatusTheCallerReceived()
    {
        // The reason request logging sits outside the exception handler. If it sat inside, the
        // status here would be 200 — the value in effect when the exception was thrown.
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri(PipelineTestHost.ThrowingPath, UriKind.Relative));

        var entry = Assert.Single(RequestEntries(recorder));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("500", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotFound_IsLoggedAtWarning_NotError()
    {
        // LG-6: Error means someone must act. A caller requesting a path that does not exist
        // is not an incident, and logging it as one trains people to ignore the error channel.
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri("/nothing-is-mapped-here", UriKind.Relative));

        Assert.Equal(LogLevel.Warning, Assert.Single(RequestEntries(recorder)).Level);
    }

    [Fact]
    public async Task RequestLog_ContainsNoQueryString()
    {
        // A query string is caller-controlled and a routine place for a token to end up.
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri($"{PipelineTestHost.SucceedingPath}?token=super-secret-value", UriKind.Relative));

        var entry = Assert.Single(RequestEntries(recorder));
        Assert.DoesNotContain("super-secret-value", entry.Message, StringComparison.Ordinal);
    }
}
