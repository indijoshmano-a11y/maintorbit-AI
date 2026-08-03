using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Notifications;

/// <summary>
/// Delivers a password reset token to the Employee who asked for it.
/// </summary>
/// <remarks>
/// <b>A seam, and at this milestone only a seam.</b> Delivery itself is blocked twice over:
/// third-party-services §7 records the email provider as pending <b>TD-4</b>, which CLAUDE.md §5
/// lists as an open decision, and describes delivery as "worker-only" — and no worker exists.
/// CLAUDE.md rule 10 says a task depending on an open decision stops there, so this port is
/// declared and the adapter behind it does not send mail.
/// <para>
/// <b>Its eventual shape is an integration event, not this call.</b> component-diagram §3 has
/// identity publish and notifications consume; backend-architecture-overview §3.6 routes that
/// through the ADR-0013 outbox, which lands with the messaging milestone. Until then a direct port
/// keeps the handler honest about what it needs, without identity reaching into another module's
/// internals — which ADR-0002 forbids and an architecture test enforces.
/// </para>
/// <para>
/// It takes the token because the message must carry it: third-party-services §7 says
/// "notification content must not contain secrets — password reset uses single-use, time-limited
/// tokens (FR-AUTH-012)". The token <i>is</i> the mechanism that makes the mail safe to send, and
/// that is why it is bounded and one-shot.
/// </para>
/// </remarks>
public interface IPasswordResetNotifier
{
    /// <summary>
    /// Sends the reset token to a verified address.
    /// </summary>
    /// <remarks>
    /// Called after the transaction commits. A message carrying a token that was rolled back is a
    /// link that fails for a legitimate Employee, and there is no way to unsend it.
    /// </remarks>
    Task SendAsync(
        Email recipient,
        EmployeeId employeeId,
        string token,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);
}
