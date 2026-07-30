namespace MaintOrbit.Shared.Abstractions;

/// <summary>
/// Provides the correlation identifier of the operation currently in flight.
/// </summary>
/// <remarks>
/// NFR-OBS-002 requires every request to carry a correlation identifier propagated across
/// all subsystems and returned to the caller, and LG-4 requires it to appear in every log
/// entry. Both are only achievable if the identifier is ambient — passing it as a parameter
/// through every method would work in theory and be abandoned within a week in practice.
/// <para>
/// The accessor is deliberately narrow. It answers "what is the current identifier" and
/// "run this operation under that identifier". It does not generate, validate, or format —
/// those belong to <c>CorrelationId</c>, so that the ingress boundary owns the decision
/// about what an inbound value is allowed to be.
/// </para>
/// <para>
/// This abstraction lives in the shared kernel rather than the application layer because
/// every layer logs, including the ones that must not know a port exists.
/// </para>
/// </remarks>
public interface ICorrelationIdAccessor
{
    /// <summary>
    /// The identifier of the operation in flight, or <see langword="null"/> outside one.
    /// </summary>
    /// <remarks>
    /// Nullable by design. Startup, shutdown, and host-level background activity genuinely
    /// have no originating request, and reporting a fabricated identifier for them would
    /// make log correlation lie rather than admit a gap.
    /// </remarks>
    string? Current { get; }

    /// <summary>
    /// Runs the enclosing operation under <paramref name="correlationId"/> until disposed.
    /// </summary>
    /// <remarks>
    /// The previous value is restored on dispose rather than cleared, so a nested operation
    /// — a Worker job spawned inside a request, say — returns its caller to the identifier
    /// it started with instead of silently losing it.
    /// </remarks>
    IDisposable BeginCorrelationScope(string correlationId);
}
