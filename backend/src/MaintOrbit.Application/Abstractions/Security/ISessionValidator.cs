using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Confirms that the session an access token names is still usable.
/// </summary>
/// <remarks>
/// A signed token proves what was true when it was issued. It cannot prove the session still
/// exists — revocation is what makes a 15-minute token safe, and a token that outlives its session
/// is exactly the case revocation exists to stop (FR-AUTH-009, NFR-SEC-017).
/// <para>
/// <b>Checked on every request.</b> 02-authentication-architecture §3.6 describes the eventual
/// mechanism as a Redis tombstone checked on every cache hit; Redis is not built, so this reads
/// the session directly. The check is the requirement; where it reads from is a later
/// optimisation — see the milestone notes on NFR-PERF-007.
/// </para>
/// </remarks>
public interface ISessionValidator
{
    /// <summary>
    /// Validates the session named by a token, cross-checking it against the token's own claims.
    /// </summary>
    /// <remarks>
    /// The Employee and Company are passed in so the session can be checked <i>against</i> them.
    /// A token whose claims disagree with the session it names has been tampered with or reissued
    /// against the wrong session, and either way must not establish a tenant context.
    /// </remarks>
    Task<Result> ValidateAsync(
        SessionId sessionId,
        EmployeeId employeeId,
        CompanyId companyId,
        CancellationToken cancellationToken);
}
