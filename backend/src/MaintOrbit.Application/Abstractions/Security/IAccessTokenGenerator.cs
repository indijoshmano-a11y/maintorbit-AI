using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Issues access tokens.
/// </summary>
/// <remarks>
/// A port, so the application layer can issue a token without knowing that JWT, a signing key, or
/// an asymmetric algorithm exist (ADR-0001). That is what allows the format to change — SD-013
/// notes the asymmetric signature "supports future key distribution" — without touching a caller.
/// <para>
/// The session identifier is a parameter rather than something resolved here. This port issues
/// tokens; it does not decide that a session exists or how long it lives.
/// </para>
/// </remarks>
public interface IAccessTokenGenerator
{
    /// <summary>
    /// Issues a short-lived access token for an authenticated Employee.
    /// </summary>
    /// <remarks>
    /// The lifetime is not a parameter. SD-013 fixes it at 15 minutes as an upper bound, and a
    /// caller able to ask for longer would eventually ask.
    /// </remarks>
    AccessToken Generate(EmployeeId employeeId, CompanyId companyId, SessionId sessionId);
}
