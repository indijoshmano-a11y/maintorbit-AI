using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the session aggregate's lifecycle rules.
/// </summary>
/// <remarks>
/// The expiry rules are the reason this aggregate reads no clock: an idle timeout is otherwise
/// only testable by waiting for one.
/// </remarks>
public sealed class SessionTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(60);

    private static Session Started(DateTimeOffset? absoluteExpiry = null) =>
        Session.Start(
            Company, Employee, SessionClientType.WebConsole,
            Start, absoluteExpiry ?? Start.AddHours(12));

    [Fact]
    public void Start_BeginsAnActiveSession()
    {
        var session = Started();

        Assert.True(session.IsActive(Start, Idle));
        Assert.False(session.IsRevoked);
    }

    [Fact]
    public void Start_SetsBothActivityTimestampsToTheStart()
    {
        var session = Started();

        Assert.Equal(Start, session.CreatedAtUtc);
        Assert.Equal(Start, session.LastActiveAtUtc);
    }

    [Fact]
    public void Start_RecordsTheDeviceForTheEmployeesOwnReview()
    {
        // FR-AUTH-008 lets an Employee see and terminate their sessions, which only works if they
        // can tell one from another.
        var session = Session.Start(
            Company, Employee, SessionClientType.VsCodeExtension, Start, Start.AddHours(12),
            deviceLabel: "Ada's laptop", ipAddress: "203.0.113.7", coarseLocation: "London, GB");

        Assert.Equal("Ada's laptop", session.DeviceLabel);
        Assert.Equal(SessionClientType.VsCodeExtension, session.ClientType);
        Assert.Equal("203.0.113.7", session.IpAddress);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Start_RejectsAnIncompleteIdentity(bool emptyCompany, bool emptyEmployee)
    {
        Assert.Throws<ArgumentException>(() => Session.Start(
            emptyCompany ? CompanyId.Empty : Company,
            emptyEmployee ? EmployeeId.Empty : Employee,
            SessionClientType.WebConsole, Start, Start.AddHours(12)));
    }

    [Fact]
    public void Start_RejectsAnExpiryAtOrBeforeTheStart()
    {
        // Such a session is never usable and is indistinguishable from one that expired normally.
        Assert.Throws<ArgumentException>(() => Session.Start(
            Company, Employee, SessionClientType.WebConsole, Start, Start));
    }

    // ---- The three timers -----------------------------------------------------------------------

    [Fact]
    public void Session_ExpiresWhenIdleTooLong()
    {
        var session = Started();

        Assert.True(session.IsActive(Start.AddMinutes(59), Idle));
        Assert.False(session.IsActive(Start.AddMinutes(61), Idle));
    }

    [Fact]
    public void Activity_ResetsTheIdleWindow()
    {
        var session = Started();

        session.RecordActivity(Start.AddMinutes(30), Idle);

        // Without the reset this would have expired at Start+60.
        Assert.True(session.IsActive(Start.AddMinutes(85), Idle));
    }

    [Fact]
    public void AbsoluteExpiry_CannotBeDefeatedByActivity()
    {
        // §3.2: "the one that cannot be defeated by activity". An attacker holding a live session
        // must not be able to extend it indefinitely by using it.
        var session = Started(Start.AddHours(2));

        for (var minute = 30; minute <= 110; minute += 30)
        {
            session.RecordActivity(Start.AddMinutes(minute), Idle);
        }

        Assert.False(session.IsActive(Start.AddHours(2).AddSeconds(1), Idle));
    }

    [Fact]
    public void Activity_IsRefusedOnAnExpiredSession()
    {
        // Reviving an expired session would make expiry advisory.
        var session = Started();

        var result = session.RecordActivity(Start.AddMinutes(61), Idle);

        Assert.True(result.IsFailure);
        Assert.Equal(Start, session.LastActiveAtUtc);
    }

    [Fact]
    public void Activity_NeverMovesBackwards()
    {
        // A clock adjustment or an out-of-order request must not shorten the idle window.
        var session = Started();
        session.RecordActivity(Start.AddMinutes(30), Idle);

        session.RecordActivity(Start.AddMinutes(10), Idle);

        Assert.Equal(Start.AddMinutes(30), session.LastActiveAtUtc);
    }

    // ---- Revocation ------------------------------------------------------------------------------

    [Fact]
    public void Revoke_EndsTheSessionAndRecordsWhy()
    {
        var session = Started();

        session.Revoke(SessionRevocationReason.LoggedOut, Start.AddMinutes(5));

        Assert.True(session.IsRevoked);
        Assert.Equal(SessionRevocationReason.LoggedOut, session.RevocationReason);
        Assert.False(session.IsActive(Start.AddMinutes(6), Idle));
    }

    [Fact]
    public void Revoke_KeepsTheFirstReason()
    {
        // A session ended by logout and later swept by a password change was ended by the logout.
        // Overwriting would erase the fact that it was already closed, which is what an
        // investigation needs.
        var session = Started();

        session.Revoke(SessionRevocationReason.LoggedOut, Start.AddMinutes(5));
        session.Revoke(SessionRevocationReason.PasswordChanged, Start.AddMinutes(9));

        Assert.Equal(SessionRevocationReason.LoggedOut, session.RevocationReason);
        Assert.Equal(Start.AddMinutes(5), session.RevokedAtUtc);
    }

    [Fact]
    public void Activity_IsRefusedOnARevokedSession()
    {
        var session = Started();
        session.Revoke(SessionRevocationReason.TerminatedByAdministrator, Start.AddMinutes(5));

        Assert.True(session.RecordActivity(Start.AddMinutes(6), Idle).IsFailure);
    }
}
