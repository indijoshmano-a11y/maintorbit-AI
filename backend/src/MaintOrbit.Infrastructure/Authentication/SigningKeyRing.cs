using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Holds the signing key and every key still accepted for validation.
/// </summary>
/// <remarks>
/// Keys are imported once. <see cref="RSA"/> instances are expensive to create and the same key is
/// used on every request, so a per-request import would put a keypair parse on the authentication
/// path for no benefit.
/// <para>
/// The set is deliberately two things at once: one key signs, several validate. That asymmetry is
/// what makes quarterly rotation (§18) survivable — a new key starts signing while tokens from the
/// old one remain valid until they expire, which is at most fifteen minutes later.
/// </para>
/// <para>
/// Registered as a singleton and disposed with the container. The private key stays in memory for
/// the process lifetime, which is unavoidable for a signer and is why the material is C4 and the
/// process is the trust boundary.
/// </para>
/// </remarks>
internal sealed class SigningKeyRing : IDisposable
{
    private readonly List<RSA> _keys = [];
    private bool _disposed;

    public SigningKeyRing(IOptions<JwtOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;

        var signing = RSA.Create();
        signing.ImportFromPem(value.SigningKey.PrivateKeyPem);
        _keys.Add(signing);

        SigningCredentials = new SigningCredentials(
            new RsaSecurityKey(signing) { KeyId = value.SigningKey.KeyId },
            // RS256. No document names an algorithm beyond "asymmetric"; RS256 is the most widely
            // interoperable choice and is what SD-013's "supports future key distribution" points
            // at. Recorded as an assumption.
            SecurityAlgorithms.RsaSha256);

        var validation = new List<SecurityKey>
        {
            new RsaSecurityKey(signing) { KeyId = value.SigningKey.KeyId }
        };

        foreach (var previous in value.PreviousKeys)
        {
            var key = RSA.Create();
            key.ImportFromPem(previous.PublicKeyPem);
            _keys.Add(key);

            validation.Add(new RsaSecurityKey(key) { KeyId = previous.KeyId });
        }

        ValidationKeys = validation;
    }

    /// <summary>Credentials for signing newly issued tokens.</summary>
    public SigningCredentials SigningCredentials { get; }

    /// <summary>Every key whose tokens are still accepted.</summary>
    public IReadOnlyCollection<SecurityKey> ValidationKeys { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var key in _keys)
        {
            key.Dispose();
        }
    }
}
