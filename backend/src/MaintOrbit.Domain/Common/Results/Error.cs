namespace MaintOrbit.Domain.Common.Results;

/// <summary>
/// A failure, carrying a stable category and a human-readable description.
/// </summary>
/// <remarks>
/// EX-2 requires errors to carry structured meaning from their origin: "a string thrown from
/// depth cannot be translated at the boundary into actionable guidance". The
/// <see cref="Code"/> is that meaning, and it is deliberately one of the categories the API
/// specification already publishes (§6.2) — clients branch on <c>type</c>, so an error invented
/// deeper in the system with a category of its own has nowhere to surface.
/// <para>
/// <see cref="Description"/> is for humans and may change; <see cref="Code"/> is the contract
/// and may not. That split is stated in §4.3 and is why a message improvement is not a breaking
/// change.
/// </para>
/// </remarks>
public sealed record Error(string Code, string Description)
{
    /// <summary>The absence of a failure.</summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>The requested resource does not exist, or is outside the caller's Company.</summary>
    /// <remarks>
    /// §6.2 defines <c>not_found</c> as covering both. The two are deliberately indistinguishable
    /// to a caller: reporting "exists, but not yours" confirms the existence of another Company's
    /// data.
    /// </remarks>
    public static Error NotFound(string description) => new("not_found", description);

    /// <summary>The operation conflicts with the current state.</summary>
    public static Error Conflict(string description) => new("conflict", description);

    /// <summary>The input failed validation.</summary>
    public static Error Validation(string description) => new("validation_failed", description);

    /// <summary>Whether this represents an actual failure.</summary>
    public bool IsNone => Code.Length == 0;
}
