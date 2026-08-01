using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Who the current request is authenticated as.
/// </summary>
/// <remarks>
/// One abstraction rather than separate Employee, Company, and session accessors: all three come
/// from the same validated token, they are established and cleared together, and three ports
/// reading one principal would let a caller hold an Employee from one request and a Company from
/// another.
/// <para>
/// <b>It carries no authorization.</b> There is no role or permission here because there is none
/// in the token — FR-PERM-005 requires a role change effective within 60 seconds, which a
/// self-contained 15-minute token cannot honour. This says who the caller is; what they may do is
/// resolved server-side per request.
/// </para>
/// <para>
/// Every property is nullable. Health probes, startup, and background work are genuinely
/// unauthenticated, and a fabricated identity for them would be worse than none.
/// </para>
/// </remarks>
public interface ICurrentIdentity
{
    /// <summary>Whether the request carries a validated access token.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The authenticated Employee, or <see langword="null"/>.</summary>
    EmployeeId? EmployeeId { get; }

    /// <summary>The Company the Employee belongs to, or <see langword="null"/>.</summary>
    CompanyId? CompanyId { get; }

    /// <summary>The session the token was issued for, or <see langword="null"/>.</summary>
    SessionId? SessionId { get; }

    /// <summary>
    /// The authenticated Employee, or a failure if the request is not authenticated.
    /// </summary>
    /// <exception cref="InvalidOperationException">The request is not authenticated.</exception>
    /// <remarks>
    /// The fail-closed call site. A caller that requires an identity uses this so forgetting a
    /// null check is impossible rather than merely unlikely.
    /// </remarks>
    EmployeeId RequireEmployeeId();

    /// <summary>The Company, or a failure if the request is not authenticated.</summary>
    /// <exception cref="InvalidOperationException">The request is not authenticated.</exception>
    CompanyId RequireCompanyId();

    /// <summary>The session, or a failure if the request is not authenticated.</summary>
    /// <exception cref="InvalidOperationException">The request is not authenticated.</exception>
    SessionId RequireSessionId();
}
