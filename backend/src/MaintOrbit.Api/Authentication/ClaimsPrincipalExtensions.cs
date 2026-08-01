using System.Security.Claims;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Api.Authentication;

/// <summary>
/// Reads the identity claims of a validated access token.
/// </summary>
/// <remarks>
/// Every read goes through here so no call site parses a claim itself. The claim names are a
/// contract shared with the issuer, and a second place that spells one of them is a second place
/// that can spell it wrong — silently, because a missing claim reads as an unauthenticated caller
/// rather than as an error.
/// <para>
/// Each accessor returns <see langword="null"/> rather than throwing on a malformed value. By the
/// time a principal exists the token has been validated, so a claim that will not parse is a
/// defect on the issuing side; refusing the request is right, and doing it by returning nothing
/// keeps that decision with the caller.
/// </para>
/// </remarks>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The Employee the token was issued to.</summary>
    public static EmployeeId? GetEmployeeId(this ClaimsPrincipal principal) =>
        ReadGuid(principal, AccessTokenClaimNames.Subject) is { } value
            ? new EmployeeId(value)
            : null;

    /// <summary>The Company the Employee belongs to.</summary>
    public static CompanyId? GetCompanyId(this ClaimsPrincipal principal) =>
        ReadGuid(principal, AccessTokenClaimNames.CompanyId) is { } value
            ? new CompanyId(value)
            : null;

    /// <summary>The session the token belongs to.</summary>
    public static SessionId? GetSessionId(this ClaimsPrincipal principal) =>
        ReadGuid(principal, AccessTokenClaimNames.SessionId) is { } value
            ? new SessionId(value)
            : null;

    private static Guid? ReadGuid(ClaimsPrincipal principal, string claimType)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirstValue(claimType);

        return Guid.TryParseExact(value, "N", out var parsed) && parsed != Guid.Empty
            ? parsed
            : null;
    }
}
