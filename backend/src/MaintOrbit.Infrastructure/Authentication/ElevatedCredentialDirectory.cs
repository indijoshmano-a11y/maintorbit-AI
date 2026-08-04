using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// The single elevated read path, implemented in raw SQL.
/// </summary>
/// <remarks>
/// Raw ADO rather than EF Core, and that is the point. Going through the <c>DbContext</c> would put
/// the whole model within reach of a path that must reach one column on four tables; hand-written
/// statements cannot drift into a join, a projection, or an <c>Include</c>. It is also legible in
/// review, which a narrow security exception has to be.
/// <para>
/// <b>How it is elevated.</b> It opens its own connection using
/// <c>Persistence:ElevatedConnectionString</c>, which a deployment points at a role permitted to
/// see across Companies. When that setting is absent it falls back to the ordinary connection —
/// correct in development, where the developer's role already sees everything, and fail-closed in
/// production, where a properly restricted application role returns no rows and sign-in visibly
/// stops working rather than silently authenticating the wrong tenant.
/// </para>
/// <para>
/// No query here returns anything actable: a Company identifier, and nothing more. No password
/// hash, no session, no employee row. Every one is a lookup by a value the caller already holds —
/// an address they typed, or a token they were sent.
/// </para>
/// </remarks>
internal sealed class ElevatedCredentialDirectory(IOptions<PersistenceOptions> options)
    : ICredentialDirectory
{
    /// <remarks>
    /// Excludes soft-deleted rows, matching <c>ux_employees_company_id_email</c>'s partial filter.
    /// </remarks>
    private const string CompanyByEmail =
        """
        SELECT company_id FROM identity.employees
        WHERE email = $1 AND deleted_at_utc IS NULL
        LIMIT 1
        """;

    /// <remarks>
    /// No filter on used or revoked: recognising a consumed token is what reuse detection is, and
    /// that decision belongs inside the tenant scope this call makes possible.
    /// </remarks>
    private const string CompanyByRefreshToken =
        """
        SELECT company_id FROM identity.refresh_tokens
        WHERE token_hash = $1
        LIMIT 1
        """;

    /// <remarks>
    /// No filter on consumed, invalidated, or expired: refusing a replayed link requires finding
    /// it, and that decision belongs inside the tenant scope this call makes possible.
    /// </remarks>
    private const string CompanyByPasswordResetToken =
        """
        SELECT company_id FROM identity.password_reset_tokens
        WHERE token_hash = $1
        LIMIT 1
        """;

    /// <remarks>
    /// No filter on consumed, invalidated, or expired, for the same reason as the reset token:
    /// refusing a replayed link requires finding it.
    /// </remarks>
    private const string CompanyByEmailVerificationToken =
        """
        SELECT company_id FROM identity.email_verification_tokens
        WHERE token_hash = $1
        LIMIT 1
        """;

    /// <inheritdoc />
    public Task<CompanyId?> FindCompanyByEmailAsync(Email email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        return ResolveAsync(CompanyByEmail, email.Value, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CompanyId?> FindCompanyByRefreshTokenAsync(
        RefreshTokenHash tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        return ResolveAsync(CompanyByRefreshToken, tokenHash.Value, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CompanyId?> FindCompanyByPasswordResetTokenAsync(
        PasswordResetTokenHash tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        return ResolveAsync(CompanyByPasswordResetToken, tokenHash.Value, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CompanyId?> FindCompanyByEmailVerificationTokenAsync(
        EmailVerificationTokenHash tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        return ResolveAsync(CompanyByEmailVerificationToken, tokenHash.Value, cancellationToken);
    }

    private async Task<CompanyId?> ResolveAsync(
        string sql, string parameter, CancellationToken cancellationToken)
    {
        var value = options.Value;

        var connectionString = string.IsNullOrWhiteSpace(value.ElevatedConnectionString)
            ? value.ConnectionString
            : value.ElevatedConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        // Parameterized. The value is caller-supplied — an email address straight from a sign-in
        // request — and this is the one place in the codebase that writes SQL by hand.
        command.Parameters.Add(new NpgsqlParameter { Value = parameter });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is Guid companyId ? new CompanyId(companyId) : null;
    }
}
