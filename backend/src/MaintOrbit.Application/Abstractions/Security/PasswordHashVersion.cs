namespace MaintOrbit.Application.Abstractions.Security;

/// <summary>
/// Which generation of Argon2id cost parameters produced a hash.
/// </summary>
/// <remarks>
/// Distinct from the Argon2 algorithm version that appears as <c>v=19</c> inside a PHC string.
/// That number identifies the algorithm revision and is fixed by the specification; this one is
/// ours, and increments whenever the annual review (SD-010) raises the cost parameters.
/// <para>
/// It exists because a parameter review has to be actionable. The PHC string already records the
/// exact costs, so correctness never depends on this value — but finding "every credential still
/// on last year's parameters" by parsing a text column across every row is not a query anyone
/// will run. <c>employee_credentials.password_version</c> makes it an indexed one.
/// </para>
/// </remarks>
public readonly record struct PasswordHashVersion(int Value)
{
    /// <summary>The first parameter generation.</summary>
    public static PasswordHashVersion Initial => new(1);

    /// <summary>Whether this is a usable generation number.</summary>
    /// <remarks>
    /// Zero is the default for an <see cref="int"/> and would mean "produced by no recorded
    /// parameter set", which is never true of a real credential.
    /// </remarks>
    public bool IsValid => Value >= 1;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
