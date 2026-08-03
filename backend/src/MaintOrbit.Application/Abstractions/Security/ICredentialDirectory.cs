using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Resolves which Company a credential belongs to, before any tenant context exists.
/// </summary>
/// <remarks>
/// <b>The only path in the system that reads across Companies.</b> Authentication has a
/// chicken-and-egg problem the rest of the application does not: row-level security needs a Company
/// in scope, and at sign-in the Company is precisely what is unknown —
/// <c>ux_employees_company_id_email</c> makes an address unique per Company, and a refresh token
/// arrives with nothing but itself.
/// <para>
/// It follows the shape 04-tenant-security §3.4 path 4 already uses for the outbox relay:
/// "processes events across Companies; runs elevated — each handler re-establishes its own Company
/// context". The same applies here. This resolves a Company and nothing else; the caller opens the
/// tenant scope immediately and every subsequent read is filtered normally.
/// </para>
/// <para>
/// <b>Deliberately unable to answer anything else.</b> It returns identifiers, never a password
/// hash, never a session, never a row a caller could act on. Widening it is how a narrow exception
/// becomes a general bypass, so the surface is two methods and the implementation is two
/// statements.
/// </para>
/// </remarks>
public interface ICredentialDirectory
{
    /// <summary>
    /// The Company an email address belongs to, or <see langword="null"/> if none does.
    /// </summary>
    /// <remarks>
    /// Soft-deleted Employees are excluded, matching the partial unique index — a removed
    /// Employee's address must not resurrect their account.
    /// </remarks>
    Task<CompanyId?> FindCompanyByEmailAsync(Email email, CancellationToken cancellationToken);

    /// <summary>
    /// The Company a refresh token belongs to, or <see langword="null"/> if no such token exists.
    /// </summary>
    /// <remarks>
    /// Returns the Company for used and revoked tokens too. Reuse detection depends on recognising
    /// a consumed token, and that check happens inside the tenant scope this makes possible —
    /// filtering here would turn a replay into "unknown token", which triggers nothing.
    /// </remarks>
    Task<CompanyId?> FindCompanyByRefreshTokenAsync(
        RefreshTokenHash tokenHash, CancellationToken cancellationToken);
}
