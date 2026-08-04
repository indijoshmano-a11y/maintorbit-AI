using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Notifications;

/// <summary>
/// Delivers a verification token to the address it is meant to prove.
/// </summary>
/// <remarks>
/// <b>A seam, and at this milestone only a seam</b>, the same as
/// <see cref="IPasswordResetNotifier"/>. Delivery is blocked twice over: third-party-services §7
/// records the email provider as pending <b>TD-4</b>, which CLAUDE.md §5 lists as an open decision,
/// and describes delivery as "worker-only" — and no worker exists.
/// <para>
/// <b>The address is the point, not an incidental parameter.</b> A verification sent anywhere other
/// than the address being verified proves nothing, so the recipient is passed explicitly rather
/// than resolved from the Employee at delivery time — which would let a later address change
/// redirect a link issued for the old one.
/// </para>
/// </remarks>
public interface IEmailVerificationNotifier
{
    /// <summary>
    /// Sends the verification token to the address it was issued for.
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
