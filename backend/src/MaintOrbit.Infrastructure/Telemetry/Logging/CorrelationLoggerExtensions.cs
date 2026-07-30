using MaintOrbit.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Infrastructure.Telemetry.Logging;

/// <summary>
/// Attaches the correlation identifier to the logging scope.
/// </summary>
/// <remarks>
/// LG-4 requires the correlation identifier in <b>every</b> log entry. Writing it into each
/// message template would satisfy the letter of that rule and fail it in practice — one
/// forgotten call site and a request becomes untraceable at exactly the point it went wrong.
/// A logging scope applies it to everything logged inside the operation, including framework
/// output the application never touches.
/// <para>
/// The scope state is a key/value collection rather than a formatted string so that
/// structured formatters emit <c>CorrelationId</c> as its own field (LG-1, NFR-OBS-001).
/// A string would be machine-readable only by regular expression, which is the thing
/// "machine-parseable" is meant to rule out.
/// </para>
/// </remarks>
public static class CorrelationLoggerExtensions
{
    /// <summary>
    /// Log property name carrying the correlation identifier.
    /// </summary>
    /// <remarks>
    /// A constant because log queries are written against it. Renaming this silently breaks
    /// every saved search and alert that refers to the old name.
    /// </remarks>
    public const string CorrelationIdPropertyName = "CorrelationId";

    /// <summary>
    /// Opens a logging scope carrying <paramref name="correlationId"/>.
    /// </summary>
    public static IDisposable? BeginCorrelationScope(this ILogger logger, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return logger.BeginScope(new Dictionary<string, object>(capacity: 1)
        {
            [CorrelationIdPropertyName] = correlationId
        });
    }

    /// <summary>
    /// Opens a logging scope carrying the identifier currently in flight, if there is one.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> outside a correlated operation. That is the honest
    /// outcome: startup and host-level activity have no originating request, and inventing an
    /// identifier for them would produce log entries that appear to belong to a request that
    /// never existed.
    /// </remarks>
    public static IDisposable? BeginCorrelationScope(
        this ILogger logger,
        ICorrelationIdAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(accessor);

        var correlationId = accessor.Current;

        return correlationId is null ? null : logger.BeginCorrelationScope(correlationId);
    }
}
