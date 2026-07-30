using System.Net;
using System.Text.Json;
using MaintOrbit.Shared.Constants;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Api.FunctionalTests.Middleware;

/// <summary>
/// Covers the error envelope produced for an unhandled exception.
/// </summary>
/// <remarks>
/// The envelope is a published contract (api-specification §4.3, §6.2). Clients branch on
/// <c>type</c>, so its value is asserted literally rather than by shape.
/// </remarks>
public sealed class ExceptionHandlingMiddlewareTests
{
    private static async Task<(HttpStatusCode Status, string? ContentType, JsonElement Body)> GetFailureAsync()
    {
        using var host = PipelineTestHost.Build();
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri(PipelineTestHost.ThrowingPath, UriKind.Relative));
        var payload = await response.Content.ReadAsStringAsync();

        return (response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                JsonDocument.Parse(payload).RootElement.Clone());
    }

    [Fact]
    public async Task UnhandledException_Returns500()
    {
        var (status, _, _) = await GetFailureAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, status);
    }

    [Fact]
    public async Task UnhandledException_ReturnsProblemJsonContentType()
    {
        var (_, contentType, _) = await GetFailureAsync();

        Assert.Equal("application/problem+json", contentType);
    }

    [Fact]
    public async Task UnhandledException_ReturnsTheDocumentedErrorCategory()
    {
        // api-specification §6.2: internal_error / 500 / retryable. `type` is the contract
        // clients branch on, so a change here is a breaking change.
        var (_, _, body) = await GetFailureAsync();

        Assert.Equal("internal_error", body.GetProperty("type").GetString());
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task UnhandledException_CarriesTheCorrelationIdentifierInTheBody()
    {
        // §4.3 requires correlationId in the envelope and §4.2 requires it in the header.
        // They must be the same value or a support conversation starts from a contradiction.
        using var host = PipelineTestHost.Build();
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri(PipelineTestHost.ThrowingPath, UriKind.Relative));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var fromHeader = Assert.Single(response.Headers.GetValues(CorrelationHeaderNames.CorrelationId));
        var fromBody = body.GetProperty("correlationId").GetString();

        Assert.Equal(fromHeader, fromBody);
    }

    [Fact]
    public async Task UnhandledException_LeaksNoInternalDetail()
    {
        // The thrown message is deliberately distinctive. An exception message is assembled
        // from whatever was in scope when it was thrown, which is the material NFR-OBS-009 and
        // EX-10 keep away from anywhere a caller can read.
        var (_, _, body) = await GetFailureAsync();
        var serialized = body.GetRawText();

        Assert.DoesNotContain("sensitive-internal-detail", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("at MaintOrbit", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledException_IsLoggedExactlyOnce_WithItsCorrelationIdentifier()
    {
        // EX-3 forbids swallowing silently; "log once" forbids the opposite. Both are asserted
        // together because a fix for either can break the other.
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri(PipelineTestHost.ThrowingPath, UriKind.Relative));
        var expected = Assert.Single(response.Headers.GetValues(CorrelationHeaderNames.CorrelationId));

        var withException = recorder.Entries
            .Where(entry => entry.Exception is InvalidOperationException)
            .ToList();

        var logged = Assert.Single(withException);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Equal(expected, logged.CorrelationId);
    }

    [Fact]
    public async Task ExceptionAfterTheResponseStarted_DoesNotProduceASuccessfulLookingBody()
    {
        // Once bytes are on the wire the status line cannot be retracted. The connection is
        // aborted so the client sees a failure rather than a truncated body that parses.
        var recorder = new RecordingLoggerProvider();
        using var host = PipelineTestHost.Build(recorder);
        await host.StartAsync();
        using var client = host.GetTestClient();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var response = await client.GetAsync(
                new Uri(PipelineTestHost.ThrowsAfterResponseStartedPath, UriKind.Relative));
            await response.Content.ReadAsStringAsync();
        });

        Assert.Single(recorder.Entries, static entry => entry.Exception is InvalidOperationException);
    }
}
