using MaintOrbit.Infrastructure.Authentication;

namespace MaintOrbit.Api.FunctionalTests.Security;

/// <summary>
/// Covers the Argon2id parameter validation.
/// </summary>
/// <remarks>
/// Every case here hashes and verifies perfectly well at runtime. The only difference a bad
/// parameter makes is how cheap an offline attack becomes, which nothing observes until a table
/// leaks — so it is caught at startup instead.
/// </remarks>
public sealed class PasswordHashingOptionsValidatorTests
{
    private static readonly PasswordHashingOptionsValidator Validator = new();

    [Fact]
    public void DefaultParameters_AreAccepted()
    {
        Assert.True(Validator.Validate(null, new PasswordHashingOptions()).Succeeded);
    }

    [Fact]
    public void MemoryBelowTheMemoryHardnessFloor_IsRejected()
    {
        // Below roughly 19 MiB the memory-hardness that justifies choosing Argon2id over a fast
        // hash stops being meaningful — SD-010 undone by a configuration value.
        var result = Validator.Validate(null, new PasswordHashingOptions
        {
            MemoryKibibytes = 8 * 1024
        });

        Assert.True(result.Failed);
        Assert.Contains(
            "memory-hardness",
            string.Join(' ', result.Failures ?? []),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MemoryTooSmallForTheLaneCount_IsRejected()
    {
        // Argon2 requires 8 KiB per lane. Without this the failure is an exception on the first
        // authentication rather than at startup.
        var result = Validator.Validate(null, new PasswordHashingOptions
        {
            MemoryKibibytes = 19 * 1024,
            Parallelism = 16
        });

        Assert.True(result.Succeeded); // 19456 KiB comfortably exceeds 16 lanes x 8 KiB
    }

    [Fact]
    public void TransposedSaltAndHashLengths_AreRejected()
    {
        // Not an algorithm requirement — a signal. A derived hash shorter than its own salt is
        // almost always the two arguments swapped, and everything still works if it is.
        var result = Validator.Validate(null, new PasswordHashingOptions
        {
            SaltLengthBytes = 32,
            HashLengthBytes = 16
        });

        Assert.True(result.Failed);
        Assert.Contains(
            "transposed",
            string.Join(' ', result.Failures ?? []),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationFailures_NameTheSettingSoItCanBeFound()
    {
        var result = Validator.Validate(null, new PasswordHashingOptions { MemoryKibibytes = 8 * 1024 });

        Assert.Contains(
            "PasswordHashing:MemoryKibibytes",
            string.Join(' ', result.Failures ?? []),
            StringComparison.Ordinal);
    }
}
