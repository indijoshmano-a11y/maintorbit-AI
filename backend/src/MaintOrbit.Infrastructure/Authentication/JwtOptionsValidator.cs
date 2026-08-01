using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Rejects JWT settings that would produce unverifiable or weak tokens.
/// </summary>
/// <remarks>
/// Every check here fails at startup rather than on the first authenticated request. A key that
/// will not parse is not a per-request error — it is a deployment that cannot issue a single valid
/// token, and it should refuse to start rather than serve 500s.
/// </remarks>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    /// <summary>
    /// Smallest RSA modulus accepted.
    /// </summary>
    /// <remarks>
    /// 2048 bits is the floor for RSA signatures in current guidance, and no document states a
    /// size. Recorded as an assumption; the alternative is accepting a key that parses, signs, and
    /// is not worth signing with.
    /// </remarks>
    private const int MinimumKeySizeBits = 2048;

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidatePrivateKey(options.SigningKey, failures);

        foreach (var previous in options.PreviousKeys)
        {
            ValidatePublicKey(previous, failures);
        }

        var keyIds = new List<string> { options.SigningKey.KeyId };
        keyIds.AddRange(options.PreviousKeys.Select(static key => key.KeyId));

        var duplicates = keyIds
            .Where(static id => !string.IsNullOrEmpty(id))
            .GroupBy(static id => id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            // A repeated kid makes key selection ambiguous, and during rotation the two keys are
            // different — so a token would verify or fail depending on which one was tried first.
            failures.Add(
                $"Jwt key identifiers must be unique. Repeated: {string.Join(", ", duplicates)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePrivateKey(JwtSigningKeyOptions key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(key.PrivateKeyPem))
        {
            // DataAnnotations already reports the empty case.
            return;
        }

        using var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(key.PrivateKeyPem);
        }
        catch (ArgumentException)
        {
            // The PEM is never echoed — it is the private key, and a validation failure is logged.
            failures.Add($"Jwt:SigningKey '{key.KeyId}' is not a readable PEM private key.");
            return;
        }

        if (!CanSign(rsa))
        {
            failures.Add(
                $"Jwt:SigningKey '{key.KeyId}' contains no private key. " +
                "A public key can validate tokens but cannot issue them.");
            return;
        }

        if (rsa.KeySize < MinimumKeySizeBits)
        {
            failures.Add(
                $"Jwt:SigningKey '{key.KeyId}' is {rsa.KeySize} bits; " +
                $"at least {MinimumKeySizeBits} are required.");
        }
    }

    private static void ValidatePublicKey(JwtValidationKeyOptions key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(key.PublicKeyPem))
        {
            return;
        }

        using var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(key.PublicKeyPem);
        }
        catch (ArgumentException)
        {
            failures.Add($"Jwt:PreviousKeys '{key.KeyId}' is not a readable PEM key.");
            return;
        }

        if (rsa.KeySize < MinimumKeySizeBits)
        {
            failures.Add(
                $"Jwt:PreviousKeys '{key.KeyId}' is {rsa.KeySize} bits; " +
                $"at least {MinimumKeySizeBits} are required.");
        }
    }

    /// <summary>
    /// Whether the key carries private material.
    /// </summary>
    /// <remarks>
    /// <c>ImportFromPem</c> accepts a public key without complaint, so a deployment given the
    /// wrong half would start and then fail on the first token issued. Exporting the private
    /// parameters is the only reliable way to ask.
    /// </remarks>
    private static bool CanSign(RSA rsa)
    {
        try
        {
            rsa.ExportParameters(includePrivateParameters: true);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
