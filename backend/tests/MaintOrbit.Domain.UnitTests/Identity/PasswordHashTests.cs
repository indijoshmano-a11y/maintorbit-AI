using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the password hash value object, chiefly its refusal to reveal itself.
/// </summary>
/// <remarks>
/// <c>employee_credentials</c> is C4 — never logged, never in error messages. LG-3 requires that
/// to hold "by construction, not masked after the fact", so these tests exercise the routes a
/// secret actually escapes by: <see cref="object.ToString"/>, string interpolation, and the
/// member printing a <c>record</c> generates for free.
/// </remarks>
public sealed class PasswordHashTests
{
    private const string Encoded = "$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA";

    [Fact]
    public void WellFormedHash_IsAccepted()
    {
        Assert.True(PasswordHash.TryCreate(Encoded, out var hash));
        Assert.Equal(Encoded, hash.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentHash_IsRejected(string? candidate)
    {
        Assert.False(PasswordHash.TryCreate(candidate, out _));
    }

    [Fact]
    public void HashCarryingAControlCharacter_IsRejected()
    {
        Assert.False(PasswordHash.TryCreate($"{Encoded}\n", out _));
    }

    [Fact]
    public void HashBeyondTheLengthCeiling_IsRejected()
    {
        Assert.False(PasswordHash.TryCreate(new string('a', PasswordHash.MaxLength + 1), out _));
    }

    [Fact]
    public void HashAtTheLengthCeiling_IsAccepted()
    {
        Assert.True(PasswordHash.TryCreate(new string('a', PasswordHash.MaxLength), out _));
    }

    // ---- C4: the value must not escape --------------------------------------------------------

    [Fact]
    public void ToString_RevealsNothing()
    {
        var hash = PasswordHash.Create(Encoded);

        Assert.DoesNotContain("argon2id", hash.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aGFzaA", hash.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", hash.ToString());
    }

    [Fact]
    public void Interpolation_RevealsNothing()
    {
        // The route a secret actually takes into a log: someone builds a message from what is in
        // scope. Interpolation calls ToString, so this is the assertion that matters most.
        var hash = PasswordHash.Create(Encoded);

        Assert.DoesNotContain("aGFzaA", $"credential={hash}", StringComparison.Ordinal);
    }

    [Fact]
    public void RecordMemberPrinting_RevealsNothing()
    {
        // A record generates a ToString built from PrintMembers, which prints every property.
        // Overriding ToString alone would leave the generated printing intact for any derived
        // record, so PrintMembers is overridden too.
        var printed = PasswordHash.Create(Encoded).ToString();

        Assert.DoesNotContain("Value", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Encoded, printed, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectionMessage_DoesNotEchoTheCandidate()
    {
        // A rejected hash is still C4 — the usual reason for rejection is truncation, so the
        // value is real material.
        var oversized = new string('z', PasswordHash.MaxLength + 1);

        var failure = Assert.Throws<ArgumentException>(() => PasswordHash.Create(oversized));

        Assert.DoesNotContain("zzz", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_IsStillReachableForThePersistenceMapping()
    {
        // Redaction must not make the type unusable. The converter and the verifier need the
        // material; everything else should have no reason to ask.
        Assert.Equal(Encoded, PasswordHash.Create(Encoded).Value);
    }
}
