using MaintOrbit.Application.Abstractions.Notifications;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Infrastructure.Notifications;

/// <summary>
/// Records that a reset token was issued, and does not send it.
/// </summary>
/// <remarks>
/// <b>Named for what it does.</b> This is not a mail adapter and must not be mistaken for one: it
/// delivers nothing, so a deployment running it has a email verification flow that issues valid tokens
/// no Employee ever receives. That is the honest state of the system at this milestone, and a
/// class called <c>SmtpEmailVerificationNotifier</c> that quietly dropped the message would be worse
/// than one that says so in its name.
/// <para>
/// <b>Two things block real delivery.</b> The email provider is pending <b>TD-4</b>, which
/// CLAUDE.md §5 lists as an open decision and rule 10 says to stop at rather than choose;
/// third-party-services §7 also records delivery as "worker-only", and no worker exists. Its
/// eventual replacement is not an SMTP client here but a notifications-module consumer of an
/// identity integration event, routed through the ADR-0013 outbox.
/// </para>
/// <para>
/// <b>It logs no token and no address.</b> The token is a live account-recovery credential and the
/// address is personal data; the log records that a reset was issued for an Employee and when the
/// link lapses, which is what an operator needs to answer "did the flow run?" without the log
/// becoming a way to take the account over.
/// </para>
/// </remarks>
internal sealed partial class UndeliveredEmailVerificationNotifier(
    ILogger<UndeliveredEmailVerificationNotifier> logger) : IEmailVerificationNotifier
{
    /// <inheritdoc />
    public Task SendAsync(
        Email recipient,
        EmployeeId employeeId,
        string token,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        EmailVerificationNotDelivered(logger, employeeId.ToString(), expiresAtUtc);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Warning,
        Message = "Password reset token issued for Employee {EmployeeId}, expiring {ExpiresAtUtc}, " +
                  "but no delivery channel is configured. The Employee will not receive it. " +
                  "Email delivery is blocked by TD-4.")]
    private static partial void EmailVerificationNotDelivered(
        ILogger logger, string employeeId, DateTimeOffset expiresAtUtc);
}
