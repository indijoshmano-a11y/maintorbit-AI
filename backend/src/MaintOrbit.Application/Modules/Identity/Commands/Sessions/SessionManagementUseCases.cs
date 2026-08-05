using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Application.Modules.Identity.Commands.Sessions;

/// <summary>One of the caller's own sessions, as their device list shows it.</summary>
/// <remarks>
/// <b>The address and location are returned deliberately.</b> §4.2 classifies them as personal
/// data about the Employee and states they are "visible to the Employee (principle P-7)" — a
/// device list that hid where a session was opened from would be unable to answer the one question
/// it exists for: is one of these not me?
/// </remarks>
public sealed record EmployeeSession(
    string Id,
    SessionClientType ClientType,
    string? DeviceLabel,
    string? IpAddress,
    string? CoarseLocation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActiveAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    bool IsCurrent);

/// <summary>The caller's own device list (FR-AUTH-008).</summary>
/// <remarks>
/// No Employee parameter. FR-AUTH-008 is a self-service capability — "Employees must be able to
/// view their active sessions" — and an identifier the caller could supply would make it a way to
/// read somebody else's device list, which is a map of where they work.
/// </remarks>
public sealed record ListSessionsQuery : IQuery<IReadOnlyList<EmployeeSession>>;

/// <summary>The session this request is authenticated with.</summary>
public sealed record GetCurrentSessionQuery : IQuery<EmployeeSession>;

/// <summary>Ends one of the caller's own sessions (FR-AUTH-008).</summary>
/// <param name="SessionId">Which session.</param>
public sealed record RevokeSessionCommand(Guid SessionId) : ICommand;

/// <summary>
/// Ends every session except the one making the request.
/// </summary>
/// <remarks>
/// §3.5's "Employee terminates all others — all except current". Distinct from
/// <c>/auth/logout-all</c>, which ends the caller's own session too: that one is "sign me out
/// everywhere", this one is "I do not recognise those devices" and must not require signing back
/// in on the device being used to say so.
/// </remarks>
public sealed record RevokeOtherSessionsCommand : ICommand<int>;

/// <summary>
/// Records genuine interaction on the current session.
/// </summary>
/// <remarks>
/// <b>An explicit command, not a side effect of every request.</b> §3.2 (SM-b) is specific: "idle
/// timeout resets on genuine user activity, not on background polling… the activity signal must
/// come from interaction, not from the SignalR connection or automatic refetches". Middleware that
/// touched <c>last_active_at_utc</c> on every request would implement exactly the behaviour that
/// sentence forbids — a console tab refreshing analytics at an unattended desk would keep the
/// session alive indefinitely.
/// <para>
/// The server cannot tell interaction from polling by looking at a request, so the client says so.
/// That places trust in the client, which is acceptable here and would not be for an authorization
/// decision: the worst a lying client achieves is keeping <i>its own</i> session alive, which the
/// absolute lifetime still bounds — §3.2 calls that "the one that cannot be defeated by activity".
/// </para>
/// </remarks>
public sealed record RecordSessionActivityCommand : ICommand;

/// <summary>Reads the caller's own sessions.</summary>
/// <remarks>
/// Applies the Company's idle window (FR-AUTH-007) rather than a guessed one, so the list shows
/// what the session validator would actually accept. A device that has idled out is gone from the
/// list before anything sweeps the row.
/// </remarks>
public sealed class ListSessionsQueryHandler(
    ICurrentIdentity currentIdentity,
    ISessionRepository sessions,
    IAuthenticationPolicyProvider policies,
    TimeProvider timeProvider)
    : IQueryHandler<ListSessionsQuery, IReadOnlyList<EmployeeSession>>
{
    public async Task<Result<IReadOnlyList<EmployeeSession>>> HandleAsync(
        ListSessionsQuery query, CancellationToken cancellationToken)
    {
        var employeeId = currentIdentity.RequireEmployeeId();
        var companyId = currentIdentity.RequireCompanyId();
        var current = currentIdentity.RequireSessionId();

        var policy = await policies.GetAsync(companyId, cancellationToken).ConfigureAwait(false);
        var idleTimeout = TimeSpan.FromMinutes(policy.IdleTimeoutMinutes);
        var now = timeProvider.GetUtcNow();

        var stored = await sessions.ListUnrevokedForEmployeeAsync(employeeId, cancellationToken)
            .ConfigureAwait(false);

        var items = stored
            .Where(session => session.IsActive(now, idleTimeout))
            .Select(session => Map(session, current))
            .ToList();

        return Result.Success<IReadOnlyList<EmployeeSession>>(items);
    }

    internal static EmployeeSession Map(Session session, SessionId current) =>
        new(session.Id.ToString(),
            session.ClientType,
            session.DeviceLabel,
            session.IpAddress,
            session.CoarseLocation,
            session.CreatedAtUtc,
            session.LastActiveAtUtc,
            session.AbsoluteExpiresAtUtc,
            IsCurrent: session.Id == current);
}

/// <summary>Reads the session this request is authenticated with.</summary>
/// <remarks>
/// The session comes from the validated token, so this cannot report anybody else's. It exists
/// because a device list is only actionable if the reader can tell which entry is the device in
/// front of them — the alternative is an Employee revoking the session they are using by mistake.
/// </remarks>
public sealed class GetCurrentSessionQueryHandler(
    ICurrentIdentity currentIdentity,
    ISessionRepository sessions)
    : IQueryHandler<GetCurrentSessionQuery, EmployeeSession>
{
    public async Task<Result<EmployeeSession>> HandleAsync(
        GetCurrentSessionQuery query, CancellationToken cancellationToken)
    {
        var sessionId = currentIdentity.RequireSessionId();

        var session = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            // The pipeline validated this session moments ago, so reaching here means it was
            // revoked in between. §6.2 makes absent and not-visible the same answer.
            return Result.Failure<EmployeeSession>(Error.NotFound("No such session."));
        }

        return Result.Success(ListSessionsQueryHandler.Map(session, sessionId));
    }
}

/// <summary>
/// Ends one of the caller's own sessions (FR-AUTH-008).
/// </summary>
/// <remarks>
/// <b>The session must belong to the caller.</b> Row-level security stops another Company's
/// session being visible at all; within a Company it does not, so the Employee is checked here.
/// Without that, any authenticated Employee could end any colleague's session by identifier —
/// which is FR-AUTH-009's administrative capability, reachable without the permission that governs
/// it.
/// <para>
/// Revoking the current session is permitted and is simply a logout. Refusing would mean an
/// Employee who suspects the device in front of them has to find a different one first.
/// </para>
/// </remarks>
public sealed class RevokeSessionCommandHandler(
    ICurrentIdentity currentIdentity,
    ISessionRepository sessions,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<RevokeSessionCommand>
{
    public async Task<Result> HandleAsync(
        RevokeSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var employeeId = currentIdentity.RequireEmployeeId();

        var session = await sessions.FindAsync(new SessionId(command.SessionId), cancellationToken)
            .ConfigureAwait(false);

        if (session is null || session.EmployeeId != employeeId)
        {
            // Answered as not-found rather than forbidden, because "that session exists but is not
            // yours" confirms a colleague is signed in — §7 requires 404 for the same reason it
            // does for a cross-tenant reference.
            return Result.Failure(Error.NotFound("No such session."));
        }

        // Idempotent in the aggregate, which keeps the first reason: a session already ended by a
        // password change was ended by that, and this must not overwrite the record.
        session.Revoke(SessionRevocationReason.TerminatedByEmployee, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            AuditActions.SessionRevoked,
            AuditOutcome.Success,
            AuditTargets.Session,
            // The value object's format, not the raw Guid's — every other identifier in an audit
            // record is written this way, and a trail that formatted one of them differently would
            // not join to the others.
            session.Id.ToString(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}

/// <summary>Ends every session except the one making the request.</summary>
public sealed class RevokeOtherSessionsCommandHandler(
    ICurrentIdentity currentIdentity,
    ISessionRepository sessions,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<RevokeOtherSessionsCommand, int>
{
    public async Task<Result<int>> HandleAsync(
        RevokeOtherSessionsCommand command, CancellationToken cancellationToken)
    {
        var employeeId = currentIdentity.RequireEmployeeId();
        var current = currentIdentity.RequireSessionId();

        var revoked = await sessions
            .RevokeAllForEmployeeExceptAsync(
                employeeId,
                current,
                SessionRevocationReason.TerminatedByEmployee,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        // The sweep is its own statement, so this commits nothing further — kept for the same
        // reason every other handler has one: when the ADR-0012 pipeline wraps this in a
        // transaction, the boundary is already where it belongs.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            AuditActions.OtherSessionsRevoked,
            AuditOutcome.Success,
            AuditTargets.Employee,
            employeeId.ToString(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["revokedCount"] = revoked.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(revoked);
    }
}

/// <summary>Records interaction on the current session, resetting the idle window.</summary>
/// <remarks>
/// Refuses on a session that is no longer active rather than reviving one. A resurrected session
/// is the failure that makes revocation meaningless, and the aggregate is where that is decided.
/// </remarks>
public sealed class RecordSessionActivityCommandHandler(
    ICurrentIdentity currentIdentity,
    ISessionRepository sessions,
    IAuthenticationPolicyProvider policies,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : ICommandHandler<RecordSessionActivityCommand>
{
    public async Task<Result> HandleAsync(
        RecordSessionActivityCommand command, CancellationToken cancellationToken)
    {
        var sessionId = currentIdentity.RequireSessionId();
        var companyId = currentIdentity.RequireCompanyId();

        var session = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return Result.Failure(Error.NotFound("No such session."));
        }

        var policy = await policies.GetAsync(companyId, cancellationToken).ConfigureAwait(false);

        var recorded = session.RecordActivity(
            timeProvider.GetUtcNow(), TimeSpan.FromMinutes(policy.IdleTimeoutMinutes));

        if (recorded.IsFailure)
        {
            return recorded;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
