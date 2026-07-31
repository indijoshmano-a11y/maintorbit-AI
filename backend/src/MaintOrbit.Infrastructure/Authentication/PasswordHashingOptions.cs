using System.ComponentModel.DataAnnotations;

namespace MaintOrbit.Infrastructure.Authentication;

/// <summary>
/// Argon2id cost parameters.
/// </summary>
/// <remarks>
/// One place holds every parameter, because they are reviewed as a set. SD-010 requires the
/// review annually — hardware improvement erodes a fixed cost, so a parameter chosen once and
/// forgotten becomes weaker every year without anything changing.
/// <para>
/// <b>These are a security decision with a denial-of-service edge</b>, which 02-authentication
/// -architecture T-5 records: every authentication attempt pays the memory and CPU cost, so
/// parameters raised without regard to concurrency turn the login endpoint into the cheapest
/// way to exhaust a host. That is why each has a validated ceiling as well as a floor.
/// </para>
/// </remarks>
public sealed class PasswordHashingOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "PasswordHashing";

    /// <summary>
    /// Memory cost in kibibytes.
    /// </summary>
    /// <remarks>
    /// The parameter that does the work. Argon2id is memory-hard precisely so that an attacker
    /// cannot trade memory for parallelism on a GPU, and lowering this is the single change that
    /// most weakens the hash. Defaults to 64 MiB.
    /// </remarks>
    [Range(8 * 1024, 1024 * 1024)]
    public int MemoryKibibytes { get; init; } = 65_536;

    /// <summary>Number of passes over memory.</summary>
    [Range(1, 10)]
    public int Iterations { get; init; } = 3;

    /// <summary>
    /// Number of lanes computed in parallel.
    /// </summary>
    /// <remarks>
    /// Bounded well below the core count of a typical host. This multiplies the work a single
    /// authentication occupies, so a high value plus concurrent attempts is the T-5 scenario.
    /// </remarks>
    [Range(1, 16)]
    public int Parallelism { get; init; } = 4;

    /// <summary>
    /// Salt length in bytes.
    /// </summary>
    /// <remarks>
    /// 16 bytes, the length RFC 9106 recommends. The salt's only job is to be unique per
    /// credential so that one derived hash says nothing about any other; 128 bits of randomness
    /// makes a collision across any realistic number of accounts negligible.
    /// </remarks>
    [Range(16, 64)]
    public int SaltLengthBytes { get; init; } = 16;

    /// <summary>Derived hash length in bytes.</summary>
    [Range(16, 64)]
    public int HashLengthBytes { get; init; } = 32;

    /// <summary>
    /// The current parameter generation, recorded on every credential this configuration writes.
    /// </summary>
    /// <remarks>
    /// Raised by hand when the parameters above change, so that credentials produced before the
    /// change can be found and upgraded. It is not derived from the parameters: a value that
    /// changed automatically would renumber history every time anything was tuned.
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int Version { get; init; } = 1;
}
