using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;

using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Application.Modules.Identity.Commands.SignOut;

/// <summary>
/// Ends the caller's own device session.
/// </summary>
/// <remarks>
/// The session comes from the validated token (<see cref="ICurrentIdentity"/>), never from the
/// request. A session identifier a caller could supply is a caller able to end somebody else's
/// session, and the tenant scope is already open by the time this runs — so the lookup is
/// filtered and a session from another Company is invisible rather than merely forbidden.
/// <para>
/// Revoking the session is enough to end the refresh chain: rotation refuses when the session is
/// not active, so every token bound to it becomes unusable without a second sweep.
/// </para>
/// </remarks>
public sealed class SignOutCommandHandler(
    ICurrentIdentity currentIdentity,
    ISessionRepository sessions,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<SignOutCommand>
{
    public async Task<Result> HandleAsync(SignOutCommand command, CancellationToken cancellationToken)
    {
        var sessionId = currentIdentity.RequireSessionId();

        var session = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            // Already gone. Signing out of a session that no longer exists is the outcome the
            // caller wanted, so it succeeds — reporting a failure would invite a retry loop
            // against a session nothing can end.
            return Result.Success();
        }

        session.Revoke(SessionRevocationReason.LoggedOut, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // §3.4 audits session termination alongside authentication. Emitted after the commit, so
        // the record describes what actually happened rather than what was about to.
        await audit.RecordAsync(
            AuditActions.SignOut,
            AuditOutcome.Success,
            AuditTargets.Session,
            sessionId.ToString(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}

/// <summary>
/// Ends every session belonging to the authenticated Employee.
/// </summary>
/// <remarks>
/// §3.5's "terminate all" (FR-AUTH-008), including the one making the request — the caller is
/// signing out everywhere, and leaving their current device signed in would be a surprising
/// reading of that.
/// </remarks>
public sealed class SignOutEverywhereCommandHandler(
    ICurrentIdentity currentIdentity,
    ISessionRepository sessions,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<SignOutEverywhereCommand>
{
    public async Task<Result> HandleAsync(
        SignOutEverywhereCommand command, CancellationToken cancellationToken)
    {
        var employeeId = currentIdentity.RequireEmployeeId();

        // Set-based: an Employee may hold many sessions, and loading each one to write a single
        // column would read an unbounded set into memory. Row-level security still applies, so
        // this reaches only the Company in scope.
        var revoked = await sessions
            .RevokeAllForEmployeeAsync(
                employeeId,
                SessionRevocationReason.TerminatedByEmployee,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            AuditActions.SignOutEverywhere,
            AuditOutcome.Success,
            AuditTargets.Employee,
            employeeId.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["revokedCount"] = revoked.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
