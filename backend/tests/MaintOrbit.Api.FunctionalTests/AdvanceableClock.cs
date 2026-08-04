namespace MaintOrbit.Api.FunctionalTests;

/// <summary>
/// A clock a test can move forward.
/// </summary>
/// <remarks>
/// Needed because TOTP's replay protection is a rule about time. Confirmation spends the step that
/// proved possession, so verifying afterwards requires a <i>later</i> step — and against the system
/// clock the only way to reach one is to wait thirty seconds per assertion. A test that slept would
/// be a test nobody runs.
/// <para>
/// Written here rather than taken from <c>Microsoft.Extensions.TimeProvider.Testing</c>, which is
/// the usual answer: that package is not in <c>docs/04-technology/backend-technologies.md</c>, and
/// the dependency policy gates additions. Overriding one method is small enough to own.
/// </para>
/// </remarks>
internal sealed class AdvanceableClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
