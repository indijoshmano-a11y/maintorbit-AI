using System.Security.Cryptography;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Derives the decoy hash once, lazily, from a random secret.
/// </summary>
/// <remarks>
/// Registered as a singleton, so the cost is paid once per process rather than once per failed
/// login — which would make the decoy itself a denial-of-service amplifier (T-5).
/// <para>
/// <see cref="Lazy{T}"/> rather than eager construction, so the cost lands on the first
/// authentication rather than on startup, where it would delay the readiness probe for no benefit.
/// </para>
/// </remarks>
internal sealed class DecoyPasswordHash : IDecoyPasswordHash
{
    private readonly Lazy<PasswordHash> _value;

    public DecoyPasswordHash(IPasswordHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        _value = new Lazy<PasswordHash>(
            () =>
            {
                // A value nobody holds, so no submitted password can match it and no attacker can
                // recognise the decoy by supplying a known input.
                Span<char> secret = stackalloc char[64];
                var random = RandomNumberGenerator.GetBytes(48);

                Convert.TryToBase64Chars(random, secret, out var written);

                try
                {
                    return hasher.Hash(secret[..written]);
                }
                finally
                {
                    secret.Clear();
                    CryptographicOperations.ZeroMemory(random);
                }
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public PasswordHash Value => _value.Value;
}
