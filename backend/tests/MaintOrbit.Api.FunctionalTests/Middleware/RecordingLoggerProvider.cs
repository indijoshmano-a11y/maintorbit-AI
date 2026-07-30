using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Api.FunctionalTests.Middleware;

/// <summary>
/// Captures log entries with their level, scopes, and exception.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopeProvider;

    public ConcurrentBag<RecordedLogEntry> Entries { get; } = [];

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            provider._scopeProvider?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var scopes = new List<object?>();
            provider._scopeProvider?.ForEachScope(
                static (scope, collected) => collected.Add(scope), scopes);

            provider.Entries.Add(new RecordedLogEntry(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception,
                scopes));
        }
    }
}

/// <summary>
/// One captured log entry.
/// </summary>
internal sealed record RecordedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<object?> Scopes)
{
    /// <summary>
    /// The correlation identifier carried by an enclosing logging scope, if any.
    /// </summary>
    public string? CorrelationId => Scopes
        .OfType<IEnumerable<KeyValuePair<string, object>>>()
        .SelectMany(static scope => scope)
        .Where(static pair => pair.Key == "CorrelationId")
        .Select(static pair => pair.Value as string)
        .FirstOrDefault();
}
