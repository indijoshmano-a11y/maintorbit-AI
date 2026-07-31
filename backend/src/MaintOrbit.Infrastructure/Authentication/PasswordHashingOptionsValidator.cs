using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Rejects Argon2id parameters that would weaken hashing or destabilise the host.
/// </summary>
/// <remarks>
/// The individual ranges are asserted by DataAnnotations. What is left here is the relationship
/// between parameters and the floor the algorithm is only worth using above — neither of which a
/// per-property attribute can express.
/// <para>
/// Every failure is one that produces no error at runtime. Parameters set too low hash
/// successfully and verify successfully; the only observable difference is how cheap an offline
/// attack becomes, which is not observable at all until a table leaks.
/// </para>
/// </remarks>
public sealed class PasswordHashingOptionsValidator : IValidateOptions<PasswordHashingOptions>
{
    /// <summary>
    /// Lowest memory cost worth calling Argon2id.
    /// </summary>
    /// <remarks>
    /// RFC 9106's low-memory profile is 64 MiB; OWASP's floor for a server is 19 MiB. Below
    /// roughly that, the memory-hardness that justifies choosing Argon2id over a fast hash stops
    /// being meaningful, and the whole SD-010 decision is undone by a configuration value.
    /// </remarks>
    private const int MinimumUsefulMemoryKibibytes = 19 * 1024;

    public ValidateOptionsResult Validate(string? name, PasswordHashingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MemoryKibibytes < MinimumUsefulMemoryKibibytes)
        {
            failures.Add(
                $"PasswordHashing:MemoryKibibytes is {options.MemoryKibibytes}, below the " +
                $"{MinimumUsefulMemoryKibibytes} KiB floor. Argon2id is chosen for memory-hardness " +
                "(SD-010); below this it provides little more than a fast hash.");
        }

        // Argon2 requires at least 8 KiB per lane. Konscious throws on a violation, which would
        // surface as an exception on the first authentication rather than at startup.
        if (options.MemoryKibibytes < options.Parallelism * 8)
        {
            failures.Add(
                $"PasswordHashing:MemoryKibibytes ({options.MemoryKibibytes}) must be at least " +
                $"8 KiB per lane for Parallelism {options.Parallelism}.");
        }

        if (options.HashLengthBytes < options.SaltLengthBytes)
        {
            // Not a hard requirement of the algorithm, but a derived hash shorter than its own
            // salt indicates the two were transposed — an easy edit to make and an expensive one
            // to notice, because everything still works.
            failures.Add(
                $"PasswordHashing:HashLengthBytes ({options.HashLengthBytes}) is shorter than " +
                $"SaltLengthBytes ({options.SaltLengthBytes}). Check the two are not transposed.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
