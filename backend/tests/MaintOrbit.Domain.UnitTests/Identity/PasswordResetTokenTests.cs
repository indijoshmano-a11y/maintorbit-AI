using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the reset token aggregate and the credential transition it drives.
/// </summary>
/// <remarks>
/// FR-AUTH-012 names three properties — single-use, time-limited, delivered by a verified email
/// flow. The first two are invariants of this aggregate and are what these assert; the third is
/// the handler's, and is covered end to end.
/// </remarks>
public sealed class PasswordResetTokenTests
{
    private static readonly CompanyId Company = new(Guid.CreateVersion7());
    private static readonly EmployeeId Employee = EmployeeId.New();
    private static readonly DateTimeOffset Requested = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expires = Requested.AddHours(1);

    private static readonly PasswordResetTokenHash Hash =
        PasswordResetTokenHash.Create(new string('a', PasswordResetTokenHash.Length));

    private static PasswordResetToken Issue() =>
        PasswordResetToken.Issue(Company, Employee, Hash, Requested, Expires);

    // ---- Issuance -----------------------------------------------------------------------------

    [Fact]
    public void AnIssuedToken_IsRedeemableAndUnspent()
    {
        var token = Issue();

        Assert.True(token.IsRedeemable(Requested));
        Assert.False(token.IsConsumed);
        Assert.False(token.IsInvalidated);
        Assert.Equal(Company, token.CompanyId);
        Assert.Equal(Employee, token.EmployeeId);
    }

    [Fact]
    public void AnIssuedToken_CarriesATimeOrderedIdentifier()
    {
        // §1.6: UUIDv7. Two issued in sequence must not collide, and must order by issue time.
        var first = Issue().Id;
        var second = Issue().Id;

        Assert.NotEqual(first, second);
        Assert.False(first.IsEmpty);
    }

    [Fact]
    public void AToken_MustBelongToACompanyAndAnEmployee()
    {
        // A reset bound to nobody resets nothing, but it would still be a live recovery
        // credential sitting in the identity schema.
        Assert.Throws<ArgumentException>(() =>
            PasswordResetToken.Issue(CompanyId.Empty, Employee, Hash, Requested, Expires));

        Assert.Throws<ArgumentException>(() =>
            PasswordResetToken.Issue(Company, EmployeeId.Empty, Hash, Requested, Expires));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void AToken_MustExpireAfterItIsRequested(int minutes)
    {
        // A window that closes before it opens is not a short window, it is a broken one — and it
        // would surface as "expired", which reads as ordinary rather than as the defect it is.
        Assert.Throws<ArgumentException>(() => PasswordResetToken.Issue(
            Company, Employee, Hash, Requested, Requested.AddMinutes(minutes)));
    }

    [Fact]
    public void TheSourceAddress_IsOptional()
    {
        // A request through a proxy chain that strips the address is still a valid request.
        Assert.Null(Issue().RequestedFromIpAddress);

        var recorded = PasswordResetToken.Issue(
            Company, Employee, Hash, Requested, Expires, "203.0.113.7");

        Assert.Equal("203.0.113.7", recorded.RequestedFromIpAddress);
    }

    // ---- Time limit ---------------------------------------------------------------------------

    [Fact]
    public void AToken_StopsBeingRedeemableAtItsExpiry()
    {
        var token = Issue();

        Assert.True(token.IsRedeemable(Expires.AddTicks(-1)));

        // Exclusive at the boundary: the instant it expires, it has.
        Assert.False(token.IsRedeemable(Expires));
        Assert.False(token.IsRedeemable(Expires.AddMinutes(1)));
    }

    [Fact]
    public void AnExpiredToken_IsStillConsumableByTheAggregate()
    {
        // Deliberate division of labour. The aggregate enforces single use; expiry is a question
        // the caller asks through IsRedeemable, because only the caller knows what time it is.
        // Folding the clock in here would make every consumption need one.
        var token = Issue();

        Assert.False(token.IsRedeemable(Expires.AddMinutes(1)));
        Assert.True(token.TryConsume(Expires.AddMinutes(1)));
    }

    // ---- Single use ---------------------------------------------------------------------------

    [Fact]
    public void AToken_IsConsumedOnce()
    {
        var token = Issue();

        Assert.True(token.TryConsume(Requested.AddMinutes(5)));
        Assert.True(token.IsConsumed);
        Assert.Equal(Requested.AddMinutes(5), token.ConsumedAtUtc);
    }

    [Fact]
    public void AReplayedToken_IsRefused()
    {
        // The replay gate, and the reason consumption returns a bool rather than throwing: the
        // second presentation is an expected event, not an exceptional one.
        var token = Issue();

        Assert.True(token.TryConsume(Requested.AddMinutes(5)));
        Assert.False(token.TryConsume(Requested.AddMinutes(6)));

        // And the record of the first redemption is not overwritten by the replay.
        Assert.Equal(Requested.AddMinutes(5), token.ConsumedAtUtc);
    }

    [Fact]
    public void AConsumedToken_IsNoLongerRedeemable()
    {
        var token = Issue();
        token.TryConsume(Requested.AddMinutes(5));

        Assert.False(token.IsRedeemable(Requested.AddMinutes(6)));
    }

    // ---- Invalidation -------------------------------------------------------------------------

    [Fact]
    public void AnInvalidatedToken_CannotBeConsumed()
    {
        // What supersession relies on. Without it an Employee could accumulate live links, each a
        // standing takeover credential for as long as it had left to run.
        var token = Issue();

        token.Invalidate(Requested.AddMinutes(1));

        Assert.True(token.IsInvalidated);
        Assert.False(token.IsRedeemable(Requested.AddMinutes(2)));
        Assert.False(token.TryConsume(Requested.AddMinutes(2)));
    }

    [Fact]
    public void Invalidation_IsIdempotentAndKeepsTheFirstInstant()
    {
        var token = Issue();

        token.Invalidate(Requested.AddMinutes(1));
        token.Invalidate(Requested.AddMinutes(9));

        Assert.Equal(Requested.AddMinutes(1), token.InvalidatedAtUtc);
    }

    [Fact]
    public void Invalidation_LeavesAConsumedTokenAlone()
    {
        // A redeemed link is spent. Stamping it as invalidated too would lose the distinction
        // between one somebody used and one that was superseded — which is the difference between
        // an ordinary recovery and an unexplained one.
        var token = Issue();
        token.TryConsume(Requested.AddMinutes(5));

        token.Invalidate(Requested.AddMinutes(6));

        Assert.Null(token.InvalidatedAtUtc);
        Assert.True(token.IsConsumed);
    }

    // ---- Redaction ----------------------------------------------------------------------------

    [Fact]
    public void ATokenHash_RefusesToPrintItself()
    {
        // C4. The hash is the lookup key for a credential that can take over an account, and a
        // record's generated ToString would put it in any log line that formats the aggregate.
        Assert.Equal("[REDACTED]", Hash.ToString());
        Assert.DoesNotContain("aaaa", $"{Hash}", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("NOTLOWERCASEHEXNOTLOWERCASEHEXNOTLOWERCASEHEXNOTLOWERCASEHEXNOTLO")]
    public void AMalformedDigest_IsRejected(string? candidate)
    {
        // SHA-256 always produces 32 bytes, so anything else did not come from the hasher — and a
        // lookup against it would match nothing, which reads as "no such token" rather than as the
        // defect it is.
        Assert.False(PasswordResetTokenHash.TryCreate(candidate, out _));
    }

    [Fact]
    public void AWellFormedDigest_IsAccepted()
    {
        Assert.True(PasswordResetTokenHash.TryCreate(new string('f', 64), out var hash));
        Assert.Equal(new string('f', 64), hash.Value);
    }

    // ---- The credential transition a reset drives ---------------------------------------------

    [Fact]
    public void ChangingThePassword_ReplacesTheHashAndRecordsWhen()
    {
        var credential = EstablishedCredential();
        var replacement = PasswordHash.Create("$argon2id$v=19$m=19456,t=3,p=1$c2FsdA$bmV3");

        credential.ChangePassword(
            replacement, PasswordAlgorithm.Argon2id, 2, "m=19456,t=3,p=1", Expires);

        Assert.Equal(replacement, credential.PasswordHash);
        Assert.Equal(2, credential.PasswordVersion);
        Assert.Equal("m=19456,t=3,p=1", credential.HashParameters);
        Assert.Equal(Expires, credential.PasswordChangedAtUtc);
        Assert.Equal(Expires, credential.UpdatedAtUtc);
    }

    [Fact]
    public void ChangingThePassword_ClearsTheLockoutState()
    {
        // A reset completed through a token delivered to the verified address is proof of control.
        // Leaving FR-AUTH-011's counter standing would lock the holder out of the password they
        // just set — turning the lockout into the denial-of-service vector 07-api-security T-3
        // warns it can become.
        var credential = EstablishedCredential();

        credential.ChangePassword(
            PasswordHash.Create("$argon2id$v=19$m=19456,t=3,p=1$c2FsdA$bmV3"),
            PasswordAlgorithm.Argon2id, 1, "m=19456,t=3,p=1", Expires);

        Assert.Equal(0, credential.FailedLoginCount);
        Assert.Null(credential.LockoutUntilUtc);
        Assert.False(credential.IsLockedOut(Expires));
        Assert.False(credential.RequirePasswordChange);
    }

    [Fact]
    public void ChangingThePassword_RefusesUnusableParameters()
    {
        // Blank parameters would leave a row that cannot be re-verified after an annual review
        // (SD-010) — the column exists precisely so an old hash stays checkable.
        var credential = EstablishedCredential();
        var replacement = PasswordHash.Create("$argon2id$v=19$m=19456,t=3,p=1$c2FsdA$bmV3");

        Assert.Throws<ArgumentException>(() => credential.ChangePassword(
            replacement, PasswordAlgorithm.Argon2id, 1, "   ", Expires));

        Assert.Throws<ArgumentOutOfRangeException>(() => credential.ChangePassword(
            replacement, PasswordAlgorithm.Argon2id, 0, "m=19456,t=3,p=1", Expires));
    }

    private static EmployeeCredential EstablishedCredential() =>
        EmployeeCredential.Establish(
            Company,
            Employee,
            PasswordHash.Create("$argon2id$v=19$m=19456,t=3,p=1$c2FsdA$b2xk"),
            PasswordAlgorithm.Argon2id,
            1,
            "m=19456,t=3,p=1",
            Requested);
}
