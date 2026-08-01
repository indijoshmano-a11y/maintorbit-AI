namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// The claim names an access token carries.
/// </summary>
/// <remarks>
/// SD-013 and api-specification §2.2 fix the claim <i>set</i> — Employee, Company, session,
/// issued-at, expiry, token type — but not the names. Registered names are used wherever one
/// exists, so the token stays readable by ordinary tooling; the two with no registered
/// equivalent are named plainly rather than under a vendor URI, which is a convention from a
/// different ecosystem and makes every claim lookup a long string.
/// <para>
/// <b>The absent names matter more than the present ones.</b> There is deliberately no claim for
/// roles or permissions. FR-PERM-005 requires a role change to take effect within 60 seconds,
/// which a self-contained 15-minute token cannot honour, and a token carrying permissions is a
/// stale authorization decision travelling around the network. Permissions are resolved
/// server-side per request.
/// </para>
/// </remarks>
public static class AccessTokenClaimNames
{
    /// <summary>The Employee the token was issued to — registered claim <c>sub</c>.</summary>
    public const string Subject = "sub";

    /// <summary>The session the token belongs to — registered claim <c>sid</c>.</summary>
    public const string SessionId = "sid";

    /// <summary>The Company the Employee belongs to.</summary>
    /// <remarks>No registered claim expresses tenancy, so this one is named plainly.</remarks>
    public const string CompanyId = "company_id";

    /// <summary>
    /// Which kind of token this is.
    /// </summary>
    /// <remarks>
    /// SD-013: "token type is a validated claim, not a convention", because a refresh token
    /// presented on an access-token path must be rejected — a real and commonly-missed confusion
    /// attack. Distinct from the JWT header's <c>typ</c>, which describes the encoding.
    /// </remarks>
    public const string TokenType = "token_type";
}

/// <summary>The values <see cref="AccessTokenClaimNames.TokenType"/> may take.</summary>
public static class AccessTokenTypes
{
    /// <summary>A short-lived bearer token for API calls.</summary>
    public const string Access = "access";
}
