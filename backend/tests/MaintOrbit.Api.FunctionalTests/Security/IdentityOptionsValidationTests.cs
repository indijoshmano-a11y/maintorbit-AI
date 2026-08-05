using System.Security.Cryptography;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Infrastructure.Authentication;
using MaintOrbit.Infrastructure.Cryptography;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Security;

/// <summary>
/// Covers the validators guarding the identity subsystem's three highest-consequence settings:
/// the token signing key, the envelope data key, and the session lifetimes.
/// </summary>
/// <remarks>
/// These share a failure mode that makes them worth testing directly. Each governs something that
/// works perfectly well when misconfigured — a 1024-bit key signs tokens, a 16-byte key encrypts
/// data, an idle timeout longer than the absolute lifetime lets everybody in. Nothing throws,
/// nothing is refused, and no request looks different. The only moment the mistake becomes visible
/// is the one it was supposed to prevent.
/// <para>
/// So the validators are the control, and until now three of the four had no test — the fourth,
/// Argon2id's, is covered by <see cref="PasswordHashingOptionsValidatorTests"/>. A control nothing
/// exercises is a control nobody would notice the removal of.
/// </para>
/// </remarks>
public sealed class IdentityOptionsValidationTests
{
    private static readonly JwtOptionsValidator Jwt = new();
    private static readonly SessionOptionsValidator Sessions = new();

    /// <summary>
    /// The encryption validator is internal to Infrastructure, which grants this assembly access
    /// rather than widening the type.
    /// </summary>
    private static readonly EncryptionOptionsValidator Encryption = new();

    // ---- Signing key -------------------------------------------------------------------------

    [Fact]
    public void AValidSigningKey_IsAccepted()
    {
        Assert.True(Jwt.Validate(null, Options(SigningKeyPem(2048))).Succeeded);
    }

    [Fact]
    public void ASigningKeyBelowTwoThousandFortyEightBits_IsRejected()
    {
        // 1024-bit RSA is within reach of a well-funded attacker, and a token forged against it is
        // a valid token — it carries a real Employee, a real Company, and a real session, and
        // every check downstream passes because the signature genuinely verifies.
        var result = Jwt.Validate(null, Options(SigningKeyPem(1024)));

        Assert.True(result.Failed);
        Assert.Contains("1024", Failures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void APublicKeyInThePrivateKeySlot_IsRejected()
    {
        // ImportFromPem accepts a public key without complaint, so this deployment would start,
        // report healthy, and fail on the first sign-in of the day.
        using var rsa = RSA.Create(2048);

        var result = Jwt.Validate(null, Options(rsa.ExportRSAPublicKeyPem()));

        Assert.True(result.Failed);
        Assert.Contains("cannot issue", Failures(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnreadableSigningKey_IsRejected()
    {
        var result = Jwt.Validate(null, Options("-----BEGIN RSA PRIVATE KEY-----not a key-----END RSA PRIVATE KEY-----"));

        Assert.True(result.Failed);
        Assert.Contains("readable PEM", Failures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureNeverEchoesTheKeyMaterial()
    {
        // The validation message is logged, and the thing being validated is the private key.
        var pem = SigningKeyPem(1024);

        var failures = Failures(Jwt.Validate(null, Options(pem)));

        Assert.DoesNotContain("PRIVATE KEY", failures, StringComparison.Ordinal);
        Assert.DoesNotContain(pem, failures, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedKeyIdentifier_IsRejected()
    {
        // During rotation the two keys are different. A repeated kid makes selection ambiguous, so
        // a token verifies or fails depending on which key is tried first — an intermittent
        // authentication failure, which is the hardest kind to attribute to configuration.
        using var previous = RSA.Create(2048);

        var result = Jwt.Validate(null, Options(
            SigningKeyPem(2048),
            new JwtValidationKeyOptions
            {
                KeyId = "current",
                PublicKeyPem = previous.ExportRSAPublicKeyPem()
            }));

        Assert.True(result.Failed);
        Assert.Contains("unique", Failures(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARetiredKeyBelowTheFloor_IsAlsoRejected()
    {
        // A weak retired key still validates tokens until it is removed, so the floor applies to
        // the whole ring rather than only to what signs today.
        using var previous = RSA.Create(1024);

        Assert.True(Jwt.Validate(null, Options(
            SigningKeyPem(2048),
            new JwtValidationKeyOptions
            {
                KeyId = "retired",
                PublicKeyPem = previous.ExportRSAPublicKeyPem()
            })).Failed);
    }

    // ---- Data key ----------------------------------------------------------------------------

    [Fact]
    public void AValidDataKey_IsAccepted()
    {
        var result = Encryption.Validate(null, new EncryptionOptions
        {
            DataKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AMissingDataKey_IsRejected()
    {
        Assert.True(Encryption.Validate(null, new EncryptionOptions()).Failed);
    }

    [Fact]
    public void ADataKeyThatIsNotBase64_IsRejected()
    {
        var result = Encryption.Validate(null, new EncryptionOptions { DataKey = "not base64!!" });

        Assert.True(result.Failed);
        Assert.Contains("base64", Failures(result), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(31)]
    public void ADataKeyShorterThanAes256_IsRejected(int length)
    {
        // SD-009 fixes AES-256. A 16-byte key is a perfectly good AES-128 key, which is the
        // problem: it would work.
        var result = Encryption.Validate(null, new EncryptionOptions
        {
            DataKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length))
        });

        Assert.True(result.Failed);
        Assert.Contains($"decodes to {length}", Failures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlongDataKey_IsRejectedForItsLengthRatherThanItsEncoding()
    {
        // A 64-byte key is a plausible mistake — it is what `openssl rand -base64 64` produces.
        // The validator has to report the length it found; reporting "not base64" for a value
        // that is unimpeachably base64 sends an operator looking for a corrupted secret instead
        // of a wrong one, and the secret is the thing they would most likely then regenerate.
        var result = Encryption.Validate(null, new EncryptionOptions
        {
            DataKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        });

        Assert.True(result.Failed);
        Assert.Contains("decodes to 64", Failures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ADataKeyFailure_NeverEchoesTheKey()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var failures = Failures(Encryption.Validate(null, new EncryptionOptions { DataKey = key }));

        Assert.DoesNotContain(key, failures, StringComparison.Ordinal);
    }

    // ---- Session lifetimes -------------------------------------------------------------------

    [Fact]
    public void TheDefaultSessionLifetimes_AreAccepted()
    {
        Assert.True(Sessions.Validate(null, new SessionOptions()).Succeeded);
    }

    [Theory]
    [InlineData(720, 720)]
    [InlineData(1_440, 720)]
    public void AnIdleWindowAtOrBeyondTheAbsoluteLifetime_IsRejected(int idle, int absolute)
    {
        // The idle timeout would exist in configuration and never fire — a control assumed to be
        // working precisely because nobody ever sees it fail. FR-AUTH-007 gives it to Companies to
        // set, so this is a mistake somebody can make from a settings screen.
        var result = Sessions.Validate(null, new SessionOptions
        {
            IdleTimeoutMinutes = idle,
            AbsoluteLifetimeMinutes = absolute
        });

        Assert.True(result.Failed);
        Assert.Contains("never take effect", Failures(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleWindowShorterThanTheAbsoluteLifetime_IsAccepted()
    {
        var result = Sessions.Validate(null, new SessionOptions
        {
            IdleTimeoutMinutes = 30,
            AbsoluteLifetimeMinutes = 480
        });

        Assert.True(result.Succeeded);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static string SigningKeyPem(int bits)
    {
        using var rsa = RSA.Create(bits);

        return rsa.ExportRSAPrivateKeyPem();
    }

    private static JwtOptions Options(
        string privateKeyPem, params JwtValidationKeyOptions[] previousKeys) => new()
    {
        Issuer = "https://localhost",
        Audience = "maintorbit-api",
        SigningKey = new JwtSigningKeyOptions { KeyId = "current", PrivateKeyPem = privateKeyPem },
        PreviousKeys = previousKeys
    };

    private static string Failures(ValidateOptionsResult result) =>
        string.Join(' ', result.Failures ?? []);
}
