using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Application.Modules.Identity.Commands.RotateRefreshToken;

/// <summary>
/// Rotates a refresh token, detecting reuse.
/// </summary>
/// <remarks>
/// SD-014: every use issues a new token, and presenting one that has already been used revokes the
/// entire family. Rotation alone only shortens a stolen token's life; reuse detection is what makes
/// theft <i>detectable</i>, because the legitimate client and the attacker inevitably both present
/// the same token and whichever arrives second gives the theft away.
/// <para>
/// Every failure returns the same error, for the same reason as login: telling a caller that a
/// token exists but is expired, or is revoked rather than unknown, describes the state of somebody
/// else's session.
/// </para>
/// </remarks>
public sealed partial class RotateRefreshTokenCommandHandler(
    IRefreshTokenRepository refreshTokens,
    ISessionRepository sessions,
    IRefreshTokenFactory tokenFactory,
    IAccessTokenGenerator accessTokens,
    IUnitOfWork unitOfWork,
    IOptions<SessionOptions> sessionOptions,
    IOptions<RefreshTokenOptions> refreshOptions,
    TimeProvider timeProvider,
    ILogger<RotateRefreshTokenCommandHandler> logger)
    : ICommandHandler<RotateRefreshTokenCommand, RefreshedTokens>
{
    private static Result<RefreshedTokens> Rejected() =>
        Result.Failure<RefreshedTokens>(
            Error.AuthenticationFailed("The refresh token is not valid."));

    public async Task<Result<RefreshedTokens>> HandleAsync(
        RotateRefreshTokenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.PresentedToken))
        {
            return Rejected();
        }

        var now = timeProvider.GetUtcNow();
        var presentedHash = tokenFactory.Hash(command.PresentedToken);

        var existing = await refreshTokens.FindByHashAsync(presentedHash, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // No such token. Indistinguishable to the caller from every other failure.
            return Rejected();
        }

        if (existing.IsUsed)
        {
            return await HandleReuseAsync(existing, now, cancellationToken).ConfigureAwait(false);
        }

        if (existing.IsRevoked || now >= existing.ExpiresAtUtc)
        {
            return Rejected();
        }

        var session = await sessions.FindAsync(existing.SessionId, cancellationToken)
            .ConfigureAwait(false);

        var idleTimeout = TimeSpan.FromMinutes(sessionOptions.Value.IdleTimeoutMinutes);

        if (session is null || !session.IsActive(now, idleTimeout))
        {
            // The session ended — revoked, idled out, or past its absolute lifetime. The token is
            // still unused, but a token outliving its session would defeat per-device revocation.
            return Rejected();
        }

        // Issue first, so the replacement's identifier can be recorded on the token it replaces.
        // That chain is what lets an investigation walk a family forwards from any member.
        var issued = tokenFactory.Issue();

        var replacement = RefreshToken.Issue(
            existing.CompanyId,
            existing.SessionId,
            existing.FamilyId,
            issued.Hash,
            now,
            now.AddMinutes(refreshOptions.Value.LifetimeMinutes));

        if (!existing.TryConsume(replacement.Id, now))
        {
            // Lost a race with a concurrent rotation of the same token. The aggregate refused
            // rather than letting both callers believe they consumed it, and the other caller is
            // now handling this as reuse.
            return Rejected();
        }

        refreshTokens.Add(replacement);

        // Rotation is genuine activity, so it resets the idle window (§3.2).
        session.RecordActivity(now, idleTimeout);

        var accessToken = accessTokens.Generate(session.EmployeeId, session.CompanyId, session.Id);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new RefreshedTokens(accessToken, issued.Token));
    }

    /// <summary>
    /// Responds to a token being presented twice.
    /// </summary>
    /// <remarks>
    /// Two parties hold the same token and there is no way to tell which is legitimate, so the
    /// whole family goes — along with the session it is bound to, since leaving that alive would
    /// let an attacker with a valid access token keep working until it expired.
    /// <para>
    /// <b>The grace window only suppresses revocation; it never returns a token.</b> §3.3 notes a
    /// legitimate race can trigger a false revocation, but the replacement token is unrecoverable
    /// once issued — so a graced replay cannot be answered with the successor. The request is still
    /// refused; the client's other tab already holds the new token. The window is zero by default
    /// because §3.3 says its length "must be measured rather than guessed".
    /// </para>
    /// </remarks>
    private async Task<Result<RefreshedTokens>> HandleReuseAsync(
        RefreshToken presented,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var grace = TimeSpan.FromSeconds(refreshOptions.Value.ReuseGraceSeconds);
        var withinGrace = grace > TimeSpan.Zero
                          && presented.UsedAtUtc is { } usedAt
                          && now - usedAt <= grace;

        if (withinGrace)
        {
            ReplayWithinGraceWindow(logger, presented.FamilyId.ToString());

            return Rejected();
        }

        RefreshTokenReuseDetected(logger, presented.FamilyId.ToString(), presented.SessionId.ToString());

        await refreshTokens.RevokeFamilyAsync(presented.FamilyId, now, cancellationToken)
            .ConfigureAwait(false);

        var session = await sessions.FindAsync(presented.SessionId, cancellationToken)
            .ConfigureAwait(false);

        session?.Revoke(SessionRevocationReason.RefreshTokenReuseDetected, now);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Rejected();
    }

    /// <remarks>
    /// Logged at <c>Warning</c>: it is a degraded condition rather than one requiring immediate
    /// action (LG-6). Repeated occurrences are the signal that the grace window is mis-tuned.
    /// </remarks>
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Refresh token replayed within the grace window for family {FamilyId}.")]
    private static partial void ReplayWithinGraceWindow(ILogger logger, string familyId);

    /// <remarks>
    /// <c>Error</c>, because this requires action: it means a refresh token was copied. FR-AUTH-014
    /// requires an audit event for authentication events, which lands with the auditing module —
    /// this log entry is what exists until then, and it deliberately carries no token material.
    /// </remarks>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Refresh token reuse detected. Revoking family {FamilyId} and session {SessionId}.")]
    private static partial void RefreshTokenReuseDetected(
        ILogger logger, string familyId, string sessionId);
}
