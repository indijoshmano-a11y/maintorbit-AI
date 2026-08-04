using System.Text;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.Authentication;
using MaintOrbit.Infrastructure.Cryptography;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Security;

/// <summary>
/// Checks the hand-written TOTP against the RFCs' own vectors, and the envelope against itself.
/// </summary>
/// <remarks>
/// backend-technologies §12 sanctions implementing TOTP rather than depending on <c>Otp.NET</c> —
/// "TOTP is a documented standard; the algorithm is straightforward to implement or substitute" —
/// and this is the price of taking that option. A TOTP implementation that is subtly wrong does
/// not fail loudly; it produces codes no authenticator app agrees with, which reads to every
/// Employee as "MFA is broken" and to nobody as a defect in dynamic truncation.
/// <para>
/// The vectors are RFC 4226 Appendix D and RFC 6238 Appendix B, both over the ASCII secret
/// <c>12345678901234567890</c>.
/// </para>
/// </remarks>
public sealed class TotpConformanceTests
{
    /// <summary>The seed both RFCs use for their published vectors.</summary>
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    private static readonly Rfc6238TotpService Totp =
        new Rfc6238TotpService(Options.Create(new MfaOptions()));

    /// <summary>An instant inside a given 30-second step.</summary>
    private static DateTimeOffset AtStep(long step) =>
        DateTimeOffset.FromUnixTimeSeconds(step * 30);

    // ---- RFC 4226 Appendix D — HOTP over counters 0..9 -----------------------------------------

    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void HotpMatchesRfc4226(long counter, string expected)
    {
        // TOTP's counter is the time step, so driving the step directly exercises RFC 4226's
        // dynamic truncation against its published table.
        Assert.True(Totp.IsValid(RfcSecret, expected, AtStep(counter)));
    }

    // ---- RFC 6238 Appendix B — TOTP at published instants ---------------------------------------

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1_111_111_109L, "081804")]
    [InlineData(1_111_111_111L, "050471")]
    [InlineData(1_234_567_890L, "005924")]
    [InlineData(2_000_000_000L, "279037")]
    [InlineData(20_000_000_000L, "353130")]
    public void TotpMatchesRfc6238(long unixSeconds, string expected)
    {
        // The RFC tabulates eight digits; six is the same value modulo a million, which is the
        // last six digits — and six is what every authenticator app shows.
        Assert.True(
            Totp.IsValid(RfcSecret, expected, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void TheLastVectorProvesTheCounterIsSixtyFourBit()
    {
        // T = 20,000,000,000 overflows a 32-bit counter. A narrower one would still produce
        // plausible six-digit codes — just the wrong ones, and only after 2038.
        Assert.True(Totp.IsValid(
            RfcSecret, "353130", DateTimeOffset.FromUnixTimeSeconds(20_000_000_000L)));
    }

    // ---- Step behaviour --------------------------------------------------------------------------

    [Fact]
    public void ACodeIsValidForItsWholeStepAndNoLonger()
    {
        var start = AtStep(1_000);

        Assert.True(Totp.IsValid(RfcSecret, CodeAt(start), start));
        Assert.True(Totp.IsValid(RfcSecret, CodeAt(start), start.AddSeconds(29)));

        // No tolerance window. RFC 6238 §5.2 permits accepting adjacent steps for clock drift and
        // none of the platform documentation specifies one, so a window here would be a number
        // chosen rather than followed — and every extra step is another window in which an
        // observed code still works.
        Assert.False(Totp.IsValid(RfcSecret, CodeAt(start), start.AddSeconds(30)));
        Assert.False(Totp.IsValid(RfcSecret, CodeAt(start), start.AddSeconds(-1)));
    }

    [Fact]
    public void TheStepBoundaryIsWhereItShouldBe()
    {
        Assert.Equal(1_000, Totp.TimeStepAt(AtStep(1_000)));
        Assert.Equal(1_000, Totp.TimeStepAt(AtStep(1_000).AddSeconds(29)));
        Assert.Equal(1_001, Totp.TimeStepAt(AtStep(1_000).AddSeconds(30)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void AMalformedCodeIsRefusedWithoutThrowing(string candidate)
    {
        // These arrive from a keyboard. A wrong shape is an ordinary failure, and an exception on
        // it would be observable in timing and in logs.
        Assert.False(Totp.IsValid(RfcSecret, candidate, AtStep(1_000)));
    }

    [Fact]
    public void ADifferentSecretProducesADifferentCode()
    {
        var other = Totp.GenerateSecret();

        Assert.False(Totp.IsValid(other, CodeAt(AtStep(1_000)), AtStep(1_000)));
    }

    // ---- Secret generation and encoding -----------------------------------------------------------

    [Fact]
    public void GeneratedSecretsAreTheConfiguredLengthAndDistinct()
    {
        var first = Totp.GenerateSecret();
        var second = Totp.GenerateSecret();

        Assert.Equal(new MfaOptions().SecretBytes, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheSecretEncodesAsBase32()
    {
        // The Key Uri Format's encoding, and what an authenticator app accepts for manual entry.
        // Base64 would be silently rejected by every app — an enrolment that appears to work and
        // then never produces a matching code.
        var encoded = Totp.Encode(RfcSecret);

        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", encoded);
        Assert.DoesNotContain("=", encoded, StringComparison.Ordinal);
    }

    // ---- Envelope encryption (SD-009) ------------------------------------------------------------

    [Fact]
    public void AnEnvelopeRoundTripsUnderTheSameCompany()
    {
        var company = new CompanyId(Guid.CreateVersion7());
        var encryptor = Encryptor();

        var envelope = encryptor.Protect(company, RfcSecret);

        Assert.Equal(RfcSecret, encryptor.Unprotect(company, envelope));
    }

    [Fact]
    public void TheCiphertextIsNotThePlaintext()
    {
        var company = new CompanyId(Guid.CreateVersion7());

        var envelope = Encryptor().Protect(company, RfcSecret);

        Assert.NotEqual(RfcSecret, envelope.Ciphertext);
        Assert.Equal(SecretEnvelope.NonceLength, envelope.Nonce.Length);
        Assert.Equal(SecretEnvelope.TagLength, envelope.AuthenticationTag.Length);
        Assert.Equal(SecretEnvelope.AesGcm256, envelope.AlgorithmId);
        Assert.Equal(1, envelope.DekVersion);
    }

    [Fact]
    public void EveryEncryptionUsesAFreshNonce()
    {
        // 09-encryption-strategy §3.8 calls this "the single most important implementation detail
        // in this document": reuse under one key allows recovery of the authentication subkey and
        // forgery of arbitrary ciphertexts. Sealing the same plaintext twice must not repeat.
        var company = new CompanyId(Guid.CreateVersion7());
        var encryptor = Encryptor();

        var first = encryptor.Protect(company, RfcSecret);
        var second = encryptor.Protect(company, RfcSecret);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void ATamperedEnvelopeDoesNotOpen()
    {
        // What SD-009's authenticated mode is for. An unauthenticated mode would decrypt this to
        // garbage and hand it to the TOTP verifier as though it were a secret.
        var company = new CompanyId(Guid.CreateVersion7());
        var encryptor = Encryptor();
        var envelope = encryptor.Protect(company, RfcSecret);

        var tampered = SecretEnvelope.Create(
            [.. envelope.Ciphertext.Select((b, i) => i == 0 ? (byte)(b ^ 0xFF) : b)],
            envelope.Nonce,
            envelope.AuthenticationTag,
            envelope.DekVersion);

        Assert.Null(encryptor.Unprotect(company, tampered));
    }

    [Fact]
    public void AnEnvelopeDoesNotOpenUnderAnotherCompany()
    {
        // The Company is bound in as additional authenticated data, so a ciphertext moved between
        // tenants fails to open rather than decrypting to the same secret under a different one.
        var encryptor = Encryptor();
        var envelope = encryptor.Protect(new CompanyId(Guid.CreateVersion7()), RfcSecret);

        Assert.Null(encryptor.Unprotect(new CompanyId(Guid.CreateVersion7()), envelope));
    }

    [Fact]
    public void AnUnknownKeyVersionDoesNotOpen()
    {
        // SD-012's version column doing its job: a row written under a key this deployment does
        // not hold is refused rather than decrypted with the wrong one.
        var company = new CompanyId(Guid.CreateVersion7());
        var encryptor = Encryptor();
        var envelope = encryptor.Protect(company, RfcSecret);

        var futureVersion = SecretEnvelope.Create(
            envelope.Ciphertext, envelope.Nonce, envelope.AuthenticationTag, dekVersion: 99);

        Assert.Null(encryptor.Unprotect(company, futureVersion));
    }

    [Fact]
    public void AnUnknownAlgorithmDoesNotOpen()
    {
        var company = new CompanyId(Guid.CreateVersion7());
        var encryptor = Encryptor();
        var envelope = encryptor.Protect(company, RfcSecret);

        var other = SecretEnvelope.Create(
            envelope.Ciphertext,
            envelope.Nonce,
            envelope.AuthenticationTag,
            envelope.DekVersion,
            algorithmId: "chacha20-poly1305");

        Assert.Null(encryptor.Unprotect(company, other));
    }

    // ---- Key configuration -----------------------------------------------------------------------

    [Theory]
    [InlineData("", "is required")]
    [InlineData("not base64 at all!", "not base64")]
    [InlineData("c2hvcnRrZXk=", "AES-256")]
    public void AnUnusableKeyIsRefusedAtStartup(string key, string expected)
    {
        // Checked at startup rather than at first use, so a misconfigured deployment fails to
        // start instead of failing on an Employee's second-factor prompt.
        var result = new EncryptionOptionsValidator()
            .Validate(null, new EncryptionOptions { DataKey = key });

        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedKeyIsAccepted()
    {
        var result = new EncryptionOptionsValidator()
            .Validate(null, new EncryptionOptions { DataKey = TestEncryptionKey.Base64 });

        Assert.True(result.Succeeded);
    }

    // ---- Recovery codes ---------------------------------------------------------------------------

    [Fact]
    public void ARecoveryCodeSetIsTheConfiguredSizeAndAllDistinct()
    {
        var options = new MfaOptions();
        var issued = new RecoveryCodeFactory(Options.Create(options)).IssueSet();

        Assert.Equal(options.RecoveryCodeCount, issued.Count);
        Assert.Equal(
            options.RecoveryCodeCount,
            issued.Select(code => code.Code).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ARecoveryCodeHashesToItsStoredDigestRegardlessOfHowItIsTyped()
    {
        // Read off a screen and typed by a person. Accepting the separator either way, and lower
        // case, avoids burning a single-use code on a transcription detail.
        var factory = new RecoveryCodeFactory(Options.Create(new MfaOptions()));
        var issued = factory.IssueSet()[0];

        Assert.Equal(issued.Hash, factory.Hash(issued.Code));
        Assert.Equal(issued.Hash, factory.Hash(issued.Code.ToLowerInvariant()));
        Assert.Equal(
            issued.Hash,
            factory.Hash(issued.Code.Replace("-", string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public void ARecoveryCodeAvoidsTheCharactersPeopleMisread()
    {
        // Crockford's alphabet without I, L, O, and U — the ones that turn into 1, 1, 0, and V on
        // a printout.
        var factory = new RecoveryCodeFactory(Options.Create(new MfaOptions()));

        foreach (var issued in factory.IssueSet())
        {
            Assert.DoesNotContain(
                issued.Code, character => character is 'I' or 'L' or 'O' or 'U');
        }
    }

    [Fact]
    public void AnIssuedCodeRefusesToPrintItself()
    {
        var issued = new RecoveryCodeFactory(Options.Create(new MfaOptions())).IssueSet()[0];

        Assert.Equal("[REDACTED]", issued.ToString());
        Assert.DoesNotContain(issued.Code, $"{issued}", StringComparison.Ordinal);
    }

    private static string CodeAt(DateTimeOffset instant)
    {
        // Recovered by search rather than exposed by the port. Producing a code is not something
        // the application ever needs to do — only checking one is — so IsValid is the whole
        // surface and this is the price of testing through it.
        for (var candidate = 0; candidate < 1_000_000; candidate++)
        {
            var text = candidate.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

            if (Totp.IsValid(RfcSecret, text, instant))
            {
                return text;
            }
        }

        throw new InvalidOperationException("No code matched, which cannot happen.");
    }

    private static AesGcmEnvelopeEncryptor Encryptor() =>
        new(new DeploymentDataKeyStore(Options.Create(
            new EncryptionOptions { DataKey = TestEncryptionKey.Base64, DataKeyVersion = 1 })));
}
