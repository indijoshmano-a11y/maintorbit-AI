using MaintOrbit.Shared.Abstractions;

namespace MaintOrbit.Infrastructure.Telemetry;

/// <summary>
/// Ambient correlation identifier backed by <see cref="AsyncLocal{T}"/>.
/// </summary>
/// <remarks>
/// <see cref="AsyncLocal{T}"/> flows with the execution context, so the identifier survives
/// every <c>await</c>, thread-pool hop, and continuation without being threaded through
/// method signatures. This is the same mechanism the framework uses for
/// <c>IHttpContextAccessor</c>, chosen for the same reason.
/// <para>
/// The backing field is <see langword="static"/>, which is what makes a singleton
/// registration correct: the instance holds no state, and the value it reads belongs to the
/// caller's execution context rather than to the object. A scoped registration would
/// allocate per request for no benefit and would leave the Worker — which has no request
/// scope — unable to correlate anything.
/// </para>
/// </remarks>
internal sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <inheritdoc />
    public string? Current => Ambient.Value;

    /// <inheritdoc />
    public IDisposable BeginCorrelationScope(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var previous = Ambient.Value;
        Ambient.Value = correlationId;

        return new AmbientScope(previous);
    }

    /// <summary>
    /// Restores the identifier that was current when the scope was opened.
    /// </summary>
    /// <remarks>
    /// Restores rather than clears. Clearing would work for the common case of one scope per
    /// request and would quietly lose the outer identifier the first time a scope nests —
    /// which is precisely the case correlation exists to keep track of.
    /// </remarks>
    private sealed class AmbientScope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
