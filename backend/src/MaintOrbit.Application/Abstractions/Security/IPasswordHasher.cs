using MaintOrbit.Domain.Modules.Identity.ValueObjects;

namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Derives and verifies password hashes.
/// </summary>
/// <remarks>
/// A port declared in the application layer and implemented in infrastructure (ADR-0001). The
/// algorithm, its cost parameters, and the encoding are all implementation detail — a caller
/// establishing or checking a credential must not be able to observe which of those is in use,
/// because the moment it can, the choice stops being replaceable.
/// <para>
/// Every method takes the plaintext as <see cref="ReadOnlySpan{T}"/> of <see cref="char"/>
/// rather than <see cref="string"/>. A <see cref="string"/> is immutable, interned in some
/// cases, and cannot be cleared, so a password held in one survives in the heap until collection
/// and may reach a memory dump. A span can be over a buffer the caller controls and overwrites.
/// </para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>
    /// The parameter generation this hasher is currently configured to produce.
    /// </summary>
    /// <remarks>
    /// Recorded on each credential as <c>password_version</c> so that a parameter review (SD-010)
    /// can find the rows it left behind. Exposed on the port because the caller writing the row
    /// must record it, and asking the caller to know it independently would let the two drift.
    /// </remarks>
    PasswordHashVersion CurrentVersion { get; }

    /// <summary>
    /// The cost parameters this hasher is currently configured with.
    /// </summary>
    /// <remarks>
    /// Stored per row as <c>hash_parameters</c>, which §4.2 requires so that a parameter change
    /// does not invalidate existing hashes. It duplicates what the PHC string already encodes,
    /// deliberately: the column is queryable, the encoded string is not.
    /// </remarks>
    string CurrentParameters { get; }

    /// <summary>
    /// Derives a hash for a new or changed password.
    /// </summary>
    /// <remarks>
    /// A fresh random salt is generated per call, so hashing the same password twice yields
    /// different output. That is what stops one leaked hash from identifying every account
    /// sharing that password.
    /// </remarks>
    PasswordHash Hash(ReadOnlySpan<char> password);

    /// <summary>
    /// Checks a password against a stored hash.
    /// </summary>
    /// <remarks>
    /// Never throws for a wrong password, a malformed hash, or unreadable parameters — those are
    /// expected outcomes and are reported through <see cref="PasswordVerificationResult"/>
    /// (EX-1). An exception here would be observable in timing and in logs, and would separate
    /// "wrong password" from "corrupt row" to anyone watching.
    /// </remarks>
    PasswordVerificationResult Verify(PasswordHash hash, ReadOnlySpan<char> password);

    /// <summary>
    /// Whether a stored hash was produced with parameters weaker than the current ones.
    /// </summary>
    /// <remarks>
    /// SD-010 reviews Argon2id parameters annually because hardware improvement erodes them. A
    /// review only takes effect if existing credentials are upgraded, and the only moment the
    /// plaintext is available to re-derive from is a successful authentication — so this is
    /// asked immediately after <see cref="Verify"/> succeeds, and never at any other time.
    /// </remarks>
    bool NeedsRehash(PasswordHash hash);
}
