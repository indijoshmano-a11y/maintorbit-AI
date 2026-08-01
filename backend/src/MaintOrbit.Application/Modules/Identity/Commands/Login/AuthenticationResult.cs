using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Modules.Identity.Commands.Login;

/// <summary>
/// A verified authenticated identity.
/// </summary>
/// <remarks>
/// Where authentication ends. It says who the caller is and which Company they belong to, and
/// nothing about how they will prove it next — no token, no session, no expiry. Those are
/// separate concerns with separate lifetimes, and binding them into this type would mean
/// authentication could not be tested, reused, or replaced without them.
/// <para>
/// The Company is carried because it is now known and was not before: TC-1 requires the tenant to
/// be derived server-side from the credential, and this is the derivation.
/// </para>
/// </remarks>
public sealed record AuthenticationResult(
    EmployeeId EmployeeId,
    CompanyId CompanyId,
    bool PasswordNeedsRehash);
