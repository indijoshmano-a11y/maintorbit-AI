using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the email verification token and the Employee transition it drives (FR-AUTH-013).
/// </summary>
/// <remarks>
/// Three properties carry the requirement: the token is single-use, it is time-limited, and it
/// proves <i>one address</i> rather than whatever address happens to be on the record when it is
/// redeemed. The third is the one a reviewer is least likely to expect and the easiest to lose.
/// </remarks>
public sealed class EmailVerificationTokenTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Holder = EmployeeId.New();
    private static readonly Email Address = Email.Create("ada@example.test");
    private static readonly DateTimeOffset Issued = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expires = Issued.AddHours(24);

    private static readonly EmailVerificationTokenHash Hash =
        EmailVerificationTokenHash.Create(new string('a', EmailVerificationTokenHash.Length));

    private static EmailVerificationToken Issue(Email? address = null) =>
        EmailVerificationToken.Issue(
            Company, Holder, address ?? Address, Hash, Issued, Expires);

    // ---- Issuance -------------------------------------------------------------------------------

    [Fact]
    public void AnIssuedTokenIsRedeemableAndUnspent()
    {
        var token = Issue();

        Assert.True(token.IsRedeemable(Issued));
        Assert.False(token.IsConsumed);
        Assert.False(token.IsInvalidated);
        Assert.Equal(Company, token.CompanyId);
        Assert.Equal(Holder, token.EmployeeId);
        Assert.Equal(Address, token.Email);
    }

    [Fact]
    public void ATokenMustBelongToACompanyAndAnEmployee()
    {
        Assert.Throws<ArgumentException>(() => EmailVerificationToken.Issue(
            CompanyId.Empty, Holder, Address, Hash, Issued, Expires));

        Assert.Throws<ArgumentException>(() => EmailVerificationToken.Issue(
            Company, EmployeeId.Empty, Address, Hash, Issued, Expires));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void ATokenMustExpireAfterItIsIssued(int minutes)
    {
        // A window that closes before it opens would surface as "expired", which reads as ordinary
        // rather than as the defect it is.
        Assert.Throws<ArgumentException>(() => EmailVerificationToken.Issue(
            Company, Holder, Address, Hash, Issued, Issued.AddMinutes(minutes)));
    }

    [Fact]
    public void AnIssuedTokenCarriesATimeOrderedIdentifier()
    {
        Assert.NotEqual(Issue().Id, Issue().Id);
        Assert.False(Issue().Id.IsEmpty);
    }

    // ---- Expiration -----------------------------------------------------------------------------

    [Fact]
    public void ATokenStopsBeingRedeemableAtItsExpiry()
    {
        var token = Issue();

        Assert.True(token.IsRedeemable(Expires.AddTicks(-1)));

        // Exclusive at the boundary: the instant it expires, it has.
        Assert.False(token.IsRedeemable(Expires));
        Assert.False(token.IsRedeemable(Expires.AddHours(1)));
    }

    // ---- One-time use ---------------------------------------------------------------------------

    [Fact]
    public void ATokenIsConsumedOnce()
    {
        var token = Issue();

        Assert.True(token.TryConsume(Issued.AddMinutes(5)));
        Assert.True(token.IsConsumed);
        Assert.Equal(Issued.AddMinutes(5), token.ConsumedAtUtc);
    }

    [Fact]
    public void AReplayedTokenIsRefused()
    {
        // The second presentation is an expected event, not an exceptional one, which is why
        // consumption returns a bool rather than throwing.
        var token = Issue();

        Assert.True(token.TryConsume(Issued.AddMinutes(5)));
        Assert.False(token.TryConsume(Issued.AddMinutes(6)));

        // And the record of the first redemption is not overwritten by the replay.
        Assert.Equal(Issued.AddMinutes(5), token.ConsumedAtUtc);
    }

    [Fact]
    public void AConsumedTokenIsNoLongerRedeemable()
    {
        var token = Issue();
        token.TryConsume(Issued.AddMinutes(5));

        Assert.False(token.IsRedeemable(Issued.AddMinutes(6)));
    }

    [Fact]
    public void AnInvalidatedTokenCannotBeConsumed()
    {
        // What supersession relies on: without it an Employee could accumulate live links, each a
        // standing proof of an address they may since have lost.
        var token = Issue();

        token.Invalidate(Issued.AddMinutes(1));

        Assert.True(token.IsInvalidated);
        Assert.False(token.IsRedeemable(Issued.AddMinutes(2)));
        Assert.False(token.TryConsume(Issued.AddMinutes(2)));
    }

    [Fact]
    public void InvalidationIsIdempotentAndLeavesAConsumedTokenAlone()
    {
        var superseded = Issue();
        superseded.Invalidate(Issued.AddMinutes(1));
        superseded.Invalidate(Issued.AddMinutes(9));

        Assert.Equal(Issued.AddMinutes(1), superseded.InvalidatedAtUtc);

        // A redeemed link is spent. Stamping it as invalidated too would lose the distinction
        // between one somebody used and one that was superseded.
        var used = Issue();
        used.TryConsume(Issued.AddMinutes(5));
        used.Invalidate(Issued.AddMinutes(6));

        Assert.Null(used.InvalidatedAtUtc);
    }

    // ---- It proves one address --------------------------------------------------------------------

    [Fact]
    public void ATokenMatchesOnlyTheAddressItWasIssuedFor()
    {
        // The check that makes a verification mean something. A link sent to an old address must
        // not verify whatever replaced it.
        var token = Issue();

        Assert.True(token.Matches(Address));
        Assert.False(token.Matches(Email.Create("someone.else@example.test")));
    }

    [Fact]
    public void MatchingCarriesTheAddressNormalization()
    {
        // Comparison is by value object, so it inherits Email's rules rather than repeating them —
        // otherwise a token issued for one casing would refuse the same address in another.
        var token = Issue(Email.Create("Ada@Example.TEST"));

        Assert.True(token.Matches(Email.Create("ada@example.test")));
    }

    // ---- Redaction --------------------------------------------------------------------------------

    [Fact]
    public void ATokenHashRefusesToPrintItself()
    {
        Assert.Equal("[REDACTED]", Hash.ToString());
        Assert.DoesNotContain("aaaa", $"{Hash}", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("NOTLOWERCASEHEXNOTLOWERCASEHEXNOTLOWERCASEHEXNOTLOWERCASEHEXNOTLO")]
    public void AMalformedDigestIsRejected(string? candidate)
    {
        Assert.False(EmailVerificationTokenHash.TryCreate(candidate, out _));
    }

    // ---- The Employee transition --------------------------------------------------------------------

    [Fact]
    public void VerifyingRecordsWhenTheAddressWasProved()
    {
        var employee = InvitedEmployee();

        Assert.Null(employee.EmailVerifiedAtUtc);

        Assert.True(employee.VerifyEmail(Issued).IsSuccess);
        Assert.Equal(Issued, employee.EmailVerifiedAtUtc);
    }

    [Fact]
    public void VerifyingDoesNotChangeStatus()
    {
        // Verification and activation are different facts. An Employee who was never activated is
        // not activated by proving their address, and a suspended one does not come back by
        // clicking a link in an old message.
        var employee = InvitedEmployee();

        employee.VerifyEmail(Issued);

        Assert.Equal(EmployeeStatus.Invited, employee.Status);
        Assert.False(employee.CanAuthenticate());
    }

    [Fact]
    public void ReVerifyingKeepsTheFirstInstant()
    {
        // EmailVerifiedAtUtc answers "how long has this address been trusted?". A column that moved
        // on every re-verification would answer it with the wrong date.
        var employee = InvitedEmployee();

        employee.VerifyEmail(Issued);
        var second = employee.VerifyEmail(Issued.AddDays(30));

        Assert.True(second.IsSuccess);
        Assert.Equal(Issued, employee.EmailVerifiedAtUtc);
    }

    [Fact]
    public void ActivationStillVerifiesTheAddressOnItsOwn()
    {
        // Accepting an invitation is itself proof — the token was emailed and came back — so the
        // two paths reach the same state by different evidence. Asserted here so this milestone's
        // new path cannot be mistaken for the only one.
        var employee = InvitedEmployee();

        Assert.True(employee.Activate(Issued).IsSuccess);

        Assert.Equal(Issued, employee.EmailVerifiedAtUtc);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
    }

    private static Employee InvitedEmployee() =>
        Employee.Invite(Company, Address, Issued);
}
