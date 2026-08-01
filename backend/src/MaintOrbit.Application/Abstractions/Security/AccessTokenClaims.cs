using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// The claims of an access token that has passed validation.
/// </summary>
/// <remarks>
/// Returned only when signature, expiry, issuer, audience, and token type have all been checked —
/// SD-013 requires every field validated and none assumed. Holding the result in its own type
/// means a caller cannot receive claims that were merely parsed.
/// <para>
/// <b>Carries no authorization.</b> There is no role or permission here because there is none in
/// the token. This says who the caller is; what they may do is resolved server-side per request.
/// </para>
/// </remarks>
public sealed record AccessTokenClaims(
    EmployeeId EmployeeId,
    CompanyId CompanyId,
    SessionId SessionId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
