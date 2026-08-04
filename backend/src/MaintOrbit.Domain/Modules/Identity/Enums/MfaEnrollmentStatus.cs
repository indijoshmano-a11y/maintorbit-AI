namespace MaintOrbit.Domain.Modules.Identity.Enums;

/// <summary>
/// Where an enrolment is in its lifecycle.
/// </summary>
/// <remarks>
/// <b>Pending is not a formality.</b> A secret is generated before the Employee has proved their
/// authenticator holds it, and an enrolment that counted from that moment would lock out anyone
/// who scanned the code into the wrong app or mistyped it — turning a second factor into a way to
/// lose an account.
/// </remarks>
public enum MfaEnrollmentStatus
{
    /// <summary>A secret was issued; possession has not been proved yet.</summary>
    Pending = 0,

    /// <summary>The Employee returned a valid code. The factor is live.</summary>
    Confirmed = 1,

    /// <summary>Superseded or turned off. Retained, never reused.</summary>
    Disabled = 2
}
