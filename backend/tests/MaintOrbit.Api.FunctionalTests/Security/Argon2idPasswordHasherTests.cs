using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.Authentication;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.FunctionalTests.Security;

/// <summary>
/// Covers Argon2id hashing and verification.
/// </summary>
/// <remarks>
/// Every test here runs the real key derivation function. Parameters are lowered to the
/// validated floor so the suite stays fast — the correctness being asserted is the round trip,
/// the encoding, and the comparison, none of which depend on the cost being production-sized.
/// </remarks>
public sealed class Argon2idPasswordHasherTests
{
    private const string Password = "correct horse battery staple";

    /// <summary>
    /// A hasher at the lowest parameters the validator accepts.
    /// </summary>
    /// <remarks>
    /// 19 MiB rather than the configured 64 MiB. At production parameters each call costs enough
    /// that a suite of them becomes slow enough to be skipped, and a skipped test protects
    /// nothing.
    /// </remarks>
    private static Argon2idPasswordHasher Hasher(
        int memory = 19 * 1024, int iterations = 1, int parallelism = 1,
        int saltLength = 16, int hashLength = 32) =>
        new Argon2idPasswordHasher(Options.Create(new PasswordHashingOptions
        {
            MemoryKibibytes = memory,
            Iterations = iterations,
            Parallelism = parallelism,
            SaltLengthBytes = saltLength,
            HashLengthBytes = hashLength
        }));

    [Fact]
    public void Hash_ProducesAPhcFormattedString()
    {
        var hash = Hasher().Hash(Password);

        Assert.StartsWith("$argon2id$v=19$", hash.Value, StringComparison.Ordinal);
        Assert.Equal(6, hash.Value.Split('$').Length);
    }

    [Fact]
    public void Hash_RecordsTheParametersItUsed()
    {
        // Self-describing output is what lets §4.2's promise hold: a parameter change must not
        // invalidate existing hashes, which is only possible if each hash carries its own costs.
        var hash = Hasher(memory: 19 * 1024, iterations: 2, parallelism: 2).Hash(Password);

        Assert.Contains("$m=19456,t=2,p=2$", hash.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_UsesAFreshSaltEachTime()
    {
        // The property that stops one leaked hash from identifying every account that shares a
        // password, and stops a precomputed table from working at all.
        var hasher = Hasher();

        var first = hasher.Hash(Password);
        var second = hasher.Hash(Password);

        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void Verify_AcceptsTheCorrectPassword()
    {
        var hasher = Hasher();
        var hash = hasher.Hash(Password);

        Assert.Equal(PasswordVerificationResult.Success, hasher.Verify(hash, Password));
    }

    [Theory]
    [InlineData("wrong horse battery staple")]
    [InlineData("correct horse battery stapl")]
    [InlineData("Correct horse battery staple")]
    [InlineData("")]
    public void Verify_RejectsAnIncorrectPassword(string attempt)
    {
        var hasher = Hasher();
        var hash = hasher.Hash(Password);

        Assert.Equal(PasswordVerificationResult.Failed, hasher.Verify(hash, attempt));
    }

    [Fact]
    public void Verify_AcceptsAHashProducedUnderDifferentParameters()
    {
        // The whole point of storing parameters per row. A credential written before an annual
        // review must keep working after it, or the review locks people out.
        var old = Hasher(memory: 19 * 1024, iterations: 1, parallelism: 1);
        var hash = old.Hash(Password);

        var current = Hasher(memory: 32 * 1024, iterations: 3, parallelism: 2);

        Assert.Equal(PasswordVerificationResult.Success, current.Verify(hash, Password));
    }

    [Theory]
    [InlineData("not-a-phc-string")]
    [InlineData("$argon2id$v=19$m=19456,t=1,p=1$c2FsdA")]              // missing the hash segment
    [InlineData("$argon2i$v=19$m=19456,t=1,p=1$c2FsdA$aGFzaA")]        // a different variant
    [InlineData("$argon2id$v=16$m=19456,t=1,p=1$c2FsdA$aGFzaA")]       // a different algorithm revision
    [InlineData("$argon2id$v=19$m=0,t=1,p=1$c2FsdA$aGFzaA")]           // a zero cost
    [InlineData("$argon2id$v=19$m=19456,t=1,p=1$$aGFzaA")]             // an empty salt
    public void Verify_ReportsAnUnreadableHashAsUnusable(string stored)
    {
        // Never throws. The value being parsed is C4, and an exception carries it outward into
        // whatever logs it.
        var result = Hasher().Verify(PasswordHash.Create(stored), Password);

        Assert.Equal(PasswordVerificationResult.Unusable, result);
    }

    [Fact]
    public void Hash_RefusesAnEmptyPassword()
    {
        // A programming error: the strength policy runs before this. Storing a credential for an
        // empty password would be an account anyone can open.
        var failure = Assert.Throws<ArgumentException>(() => Hasher().Hash(ReadOnlySpan<char>.Empty));

        Assert.Contains("empty", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_ReturnsFailedForAnEmptyPassword_RatherThanThrowing()
    {
        // The underlying library raises on an empty password. Letting that escape would turn a
        // routine failed attempt into an exception on the authentication path — visible in logs,
        // in metrics, and in response timing.
        var hasher = Hasher();

        Assert.Equal(
            PasswordVerificationResult.Failed,
            hasher.Verify(hasher.Hash(Password), ReadOnlySpan<char>.Empty));
    }

    [Fact]
    public void Verify_DistinguishesAWrongPasswordFromAnUnusableHash()
    {
        // A wrong guess is an ordinary failed attempt; an unreadable row is an operational
        // fault. Collapsing them into false would hide a corrupted credential table.
        var hasher = Hasher();

        Assert.NotEqual(
            hasher.Verify(hasher.Hash(Password), "wrong"),
            hasher.Verify(PasswordHash.Create("garbage"), "wrong"));
    }

    // ---- NeedsRehash ---------------------------------------------------------------------------

    [Fact]
    public void NeedsRehash_IsFalseForAHashAtTheCurrentParameters()
    {
        var hasher = Hasher();

        Assert.False(hasher.NeedsRehash(hasher.Hash(Password)));
    }

    [Theory]
    [InlineData(32 * 1024, 1, 1)]   // memory raised
    [InlineData(19 * 1024, 3, 1)]   // iterations raised
    [InlineData(19 * 1024, 1, 2)]   // parallelism raised
    public void NeedsRehash_IsTrueWhenAnyParameterWasRaised(int memory, int iterations, int parallelism)
    {
        // Each parameter contributes independently, so weaker on any single axis is below the
        // reviewed standard even if the others match.
        var hash = Hasher(memory: 19 * 1024, iterations: 1, parallelism: 1).Hash(Password);

        var current = Hasher(memory: memory, iterations: iterations, parallelism: parallelism);

        Assert.True(current.NeedsRehash(hash));
    }

    [Fact]
    public void NeedsRehash_IsFalseWhenParametersWereLowered()
    {
        // A credential stronger than the current configuration is not weak. Rehashing it would
        // downgrade it.
        var hash = Hasher(memory: 32 * 1024, iterations: 3, parallelism: 2).Hash(Password);

        Assert.False(Hasher(memory: 19 * 1024, iterations: 1, parallelism: 1).NeedsRehash(hash));
    }

    [Fact]
    public void NeedsRehash_IsTrueForAnUnreadableHash()
    {
        // It cannot be shown to meet the current parameters, so the next successful
        // authentication should replace it.
        Assert.True(Hasher().NeedsRehash(PasswordHash.Create("not-a-phc-string")));
    }

    // ---- Leakage --------------------------------------------------------------------------------

    [Fact]
    public void NothingInTheHash_ContainsThePlaintext()
    {
        var hash = Hasher().Hash(Password);

        Assert.DoesNotContain("correct", hash.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("staple", hash.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHashStillRefusesToPrintItself()
    {
        // The redaction from 11.3 must survive a real hash passing through it.
        var hash = Hasher().Hash(Password);

        Assert.Equal("[REDACTED]", hash.ToString());
        Assert.DoesNotContain("argon2id", $"{hash}", StringComparison.OrdinalIgnoreCase);
    }

    // ---- Registration ---------------------------------------------------------------------------

    [Fact]
    public void Hasher_ResolvesFromTheCompositionRootAsASingleton()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Application:Name"] = "MaintOrbit AI",
                ["Application:PublicBaseUrl"] = "https://api.example.test",
                ["Cors:AllowCredentials"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                ["Persistence:ConnectionString"] =
                    "Host=localhost;Database=maintorbit_test;Username=maintorbit"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddApplication().AddInfrastructure(configuration)
            .AddApi(configuration).AddObservability(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            second.ServiceProvider.GetRequiredService<IPasswordHasher>());
    }
}
