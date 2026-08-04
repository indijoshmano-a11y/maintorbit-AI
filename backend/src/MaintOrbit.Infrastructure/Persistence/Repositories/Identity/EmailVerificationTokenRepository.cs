using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MaintOrbit.Infrastructure.Persistence.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="IEmailVerificationTokenRepository"/>.</summary>
internal sealed class EmailVerificationTokenRepository(MaintOrbitDbContext context)
    : IEmailVerificationTokenRepository
{
    /// <inheritdoc />
    public Task<EmailVerificationToken?> FindByHashAsync(
        EmailVerificationTokenHash hash, CancellationToken cancellationToken) =>
        // Tracked: the caller consumes the token it finds, and an untracked aggregate would be
        // marked consumed in memory and never written — which would leave every redeemed link
        // live for a second use, the one property FR-AUTH-013's proof turns on.
        context.EmailVerificationTokens.FirstOrDefaultAsync(
            token => token.TokenHash == hash, cancellationToken);

    /// <inheritdoc />
    public void Add(EmailVerificationToken token) => context.EmailVerificationTokens.Add(token);

    /// <inheritdoc />
    public Task<int> InvalidateOutstandingForEmployeeAsync(
        EmployeeId employeeId,
        DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken) =>
        // Set-based, and it excludes rows already consumed or invalidated: a spent link is spent,
        // and overwriting its timestamps would lose whether it was used or superseded. Row-level
        // security still applies, so this reaches only the Company in scope.
        context.EmailVerificationTokens
            .Where(token =>
                token.EmployeeId == employeeId &&
                token.ConsumedAtUtc == null &&
                token.InvalidatedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.InvalidatedAtUtc, invalidatedAtUtc),
                cancellationToken);
}
