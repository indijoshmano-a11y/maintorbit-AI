namespace MaintOrbit.Domain.Modules.Identity.Enums;

/// <summary>
/// The key derivation function that produced a stored password hash.
/// </summary>
/// <remarks>
/// One member, and that is the correct size. SD-010 and compliance §14 name <b>Argon2id</b> and
/// nothing else, and this milestone makes no algorithm decisions beyond the documented one.
/// <para>
/// It exists as an enum rather than being assumed because the column is what makes a future
/// migration possible: an algorithm change has to leave existing hashes verifiable until each
/// Employee next authenticates, which means every row must say how it was produced. A codebase
/// that assumes one algorithm has to guess during exactly the transition where guessing is worst.
/// </para>
/// <para>
/// Applies to passwords only. Platform API Keys are hashed with SHA-256 — 09-encryption-strategy
/// §3 is explicit that a memory-hard function there adds cost per Gateway request without adding
/// security, because a random key is not guessable regardless.
/// </para>
/// </remarks>
public enum PasswordAlgorithm
{
    /// <summary>
    /// Argon2id (SD-010), with parameters reviewed annually.
    /// </summary>
    /// <remarks>
    /// Memory-hard, so it resists the GPU and ASIC acceleration that makes offline guessing
    /// cheap against a fast hash.
    /// </remarks>
    Argon2id = 0
}
