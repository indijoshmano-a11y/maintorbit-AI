using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// A real password hash that matches nothing, used to keep failed authentication costly.
/// </summary>
/// <remarks>
/// Closes a user-enumeration oracle. Argon2id is deliberately expensive, so a login that returns
/// before reaching it is measurably faster than one that does not — and "unknown address" is
/// exactly the case with nothing to verify. An attacker submitting addresses and timing the
/// responses learns which ones exist, without ever guessing a password.
/// <para>
/// Verifying against this hash instead makes the miss path pay the same cost as the hit path. The
/// answer is discarded; only the work matters. §6.2 gives one <c>authentication_failed</c>
/// category for every credential failure, and threat I-13 requires uniform responses — this is
/// what makes the response uniform in duration as well as in content.
/// </para>
/// <para>
/// Derived once, at the parameters currently configured, from a value nobody knows. Both
/// properties are necessary: hard-coding a hash would fix its cost at whatever parameters were
/// current when it was written, and a known input would let an attacker recognise the decoy.
/// </para>
/// </remarks>
public interface IDecoyPasswordHash
{
    /// <summary>The reference hash. It matches no password anyone can supply.</summary>
    PasswordHash Value { get; }
}
