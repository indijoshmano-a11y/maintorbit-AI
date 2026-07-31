using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Domain.UnitTests.Identity;

/// <summary>
/// Covers the email value object.
/// </summary>
/// <remarks>
/// Normalization carries more weight here than validation. The uniqueness rule is enforced by a
/// database index over the stored text, so two spellings of one address that normalize
/// differently become two Employees of the same Company — which is not a validation failure, it
/// is a duplicate account.
/// </remarks>
public sealed class EmailTests
{
    [Theory]
    [InlineData("ada@example.com")]
    [InlineData("ada.lovelace@sub.example.co.uk")]
    [InlineData("ada+work@example.com")]
    [InlineData("a@b.co")]
    public void WellFormedAddresses_AreAccepted(string candidate)
    {
        Assert.True(Email.TryCreate(candidate, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("@example.com")]
    [InlineData("ada@")]
    [InlineData("ada@@example.com")]
    [InlineData("ada@example@com")]
    [InlineData("ada@nodot")]
    [InlineData("ada@.example.com")]
    [InlineData("ada@example.com.")]
    [InlineData("ada@exa..mple.com")]
    [InlineData("ada lovelace@example.com")]
    public void MalformedAddresses_AreRejected(string? candidate)
    {
        Assert.False(Email.TryCreate(candidate, out _));
    }

    [Fact]
    public void AddressCarryingALineBreak_IsRejected()
    {
        // The address is written into log context and into the invitation email. A control
        // character in a line-oriented log lets a caller forge entries.
        Assert.False(Email.TryCreate("ada@example.com\nX-Injected: true", out _));
    }

    [Theory]
    [InlineData("ADA@EXAMPLE.COM", "ada@example.com")]
    [InlineData("  ada@example.com  ", "ada@example.com")]
    [InlineData("Ada.Lovelace@Example.Com", "ada.lovelace@example.com")]
    public void AddressesAreNormalized(string candidate, string expected)
    {
        Assert.True(Email.TryCreate(candidate, out var email));
        Assert.Equal(expected, email.Value);
    }

    [Fact]
    public void AddressesDifferingOnlyByCase_AreEqual()
    {
        // The property that makes ux_employees_company_id_email mean what it is supposed to mean.
        Assert.True(Email.TryCreate("Ada@Example.com", out var upper));
        Assert.True(Email.TryCreate("ada@example.com", out var lower));

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void UppercaseAsciiNormalizesToLowercase()
    {
        // Normalization uses ToLowerInvariant. The scenario that makes the distinction matter —
        // a Turkish culture mapping 'I' to a dotless 'i', so one address normalizes two ways
        // depending on where the server runs — cannot be reproduced here, because
        // InvariantGlobalization is enabled solution-wide. The invariant call is kept regardless:
        // it is correct if that setting is ever revisited, and free if it is not.
        Assert.True(Email.TryCreate("INDIGO@EXAMPLE.COM", out var email));
        Assert.Equal("indigo@example.com", email.Value);
    }

    [Fact]
    public void AddressAtTheLengthCeiling_IsAccepted()
    {
        const string Suffix = "@example.com";
        var candidate = new string('a', Email.MaxLength - Suffix.Length) + Suffix;

        Assert.Equal(Email.MaxLength, candidate.Length);
        Assert.True(Email.TryCreate(candidate, out _));
    }

    [Fact]
    public void AddressBeyondTheLengthCeiling_IsRejected()
    {
        const string Suffix = "@example.com";
        var candidate = new string('a', Email.MaxLength - Suffix.Length + 1) + Suffix;

        Assert.False(Email.TryCreate(candidate, out _));
    }

    [Fact]
    public void Create_ThrowsWithoutEchoingTheAddress()
    {
        // An email address is personal data and the exception message reaches logs (LG-2).
        var failure = Assert.Throws<ArgumentException>(
            () => Email.Create("ada.lovelace@invalid"));

        Assert.DoesNotContain("ada.lovelace", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
