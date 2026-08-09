namespace MaintOrbit.Domain.Modules.Auditing;

/// <summary>
/// The guard on what may enter an Audit Event's context.
/// </summary>
/// <remarks>
/// <b>Audit records are the one store designed never to be deleted.</b> Everything else has a
/// retention period, a purge, or a soft-delete flag; audit events are append-only, retained for at
/// least twelve months, exported to customers (AU-6), and read by auditors. A credential that
/// reaches this column is a credential that cannot be removed by any code path the system has,
/// because AU-1 removed those paths on purpose.
/// <para>
/// That asymmetry is why sanitization lives here — in the domain, at construction — rather than at
/// each emission point. There are thirteen emission points today and more with every module; a
/// convention applied at each is a convention that holds until somebody adds the fourteenth.
/// <see cref="AuditEvent.Record"/> cannot be called without passing through this.
/// </para>
/// <para>
/// <b>Redacts rather than throws.</b> Emission is fail-open (ADR-0021, SD-004), so an exception
/// here would discard the whole event and leave only an AU-8 incident — the record of a
/// permission denial lost because one of its context values was named "token". Redacting keeps
/// the event, which is the part that matters, and drops only the value that should never have
/// been offered.
/// </para>
/// </remarks>
public static class AuditContext
{
    /// <summary>What replaces a value that must not be stored.</summary>
    public const string Redacted = "[REDACTED]";

    /// <summary>
    /// The longest value a context entry may carry.
    /// </summary>
    /// <remarks>
    /// AU-4 forbids prompt and completion content, and §8.5 expects small scalars and references —
    /// identifiers, counts, flags, before-and-after configuration values. A cap is the structural
    /// way to say that: a request body, a completion, or a stack trace does not fit, so the column
    /// cannot quietly become a general-purpose payload. Values over the cap are truncated with a
    /// marker rather than dropped, so the reader can see something was cut.
    /// </remarks>
    public const int MaximumValueLength = 512;

    /// <summary>Appended to a value the cap has shortened.</summary>
    public const string Truncated = "…[truncated]";

    /// <summary>
    /// Key fragments that mark a value as credential material.
    /// </summary>
    /// <remarks>
    /// Matched as a case-insensitive substring of the key, so <c>refreshToken</c>,
    /// <c>Authorization</c> and <c>client_secret</c> are all caught without enumerating spellings.
    /// <para>
    /// <b>Deliberately over-broad.</b> A false positive redacts one diagnostic value in one record;
    /// a false negative writes a credential into a store with no delete path. Those costs are not
    /// comparable, so the list errs toward redaction — which is also why the redaction is visible
    /// rather than silent, giving anyone who hits a false positive an obvious thing to rename.
    /// </para>
    /// </remarks>
    private static readonly string[] CredentialKeyFragments =
    [
        "password", "passphrase", "secret", "token", "credential", "authorization",
        "cookie", "session_key", "apikey", "api_key", "privatekey", "private_key",
        "hash", "salt", "signature", "bearer", "otp", "pin", "recoverycode",
        "recovery_code", "seed", "nonce", "assertion"
    ];

    /// <summary>
    /// Returns a context safe to store, or <see langword="null"/> if there is nothing to store.
    /// </summary>
    /// <remarks>
    /// Copies rather than wrapping. The caller's dictionary is theirs and may be mutated after
    /// this returns; an audit record that changed after it was written would defeat the point of
    /// the store it goes into.
    /// </remarks>
    public static IReadOnlyDictionary<string, string>? Sanitize(
        IReadOnlyDictionary<string, string>? context)
    {
        if (context is null || context.Count == 0)
        {
            return null;
        }

        var sanitized = new Dictionary<string, string>(context.Count, StringComparer.Ordinal);

        foreach (var (key, value) in context)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                // A nameless value cannot be interpreted by a reader and cannot be searched by
                // AU-5, so it is not worth the risk of storing.
                continue;
            }

            var raw = value ?? string.Empty;

            sanitized[key] = IsCredentialShaped(key) && !IsBoolean(raw) ? Redacted : Cap(raw);
        }

        return sanitized;
    }

    /// <summary>
    /// Whether a key names something that must never be stored.
    /// </summary>
    /// <remarks>
    /// Exposed so the security tests can state the rule directly against the list rather than
    /// inferring it from a sanitized result, and so a caller choosing a context key can check
    /// their own spelling.
    /// </remarks>
    public static bool IsCredentialShaped(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return CredentialKeyFragments.Any(
            fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a value is a boolean, and therefore cannot be carrying a credential.
    /// </summary>
    /// <remarks>
    /// <b>The one exemption, and it exists because the guard was damaging a real signal.</b> The
    /// MFA challenge records <c>usedRecoveryCode</c> — a flag, not a code — and §3.4 wants exactly
    /// that: a run of recovery-code authentications is somebody who has lost their authenticator,
    /// or somebody who never had it. A key-name rule alone redacted the flag and destroyed the
    /// signal.
    /// <para>
    /// Restricted to booleans rather than to short values generally. A PIN, a one-time code, or a
    /// truncated key can all look like a small number; none of them can look like
    /// <c>True</c> or <c>False</c>. That is what makes this exemption safe to state and cheap to
    /// check.
    /// </para>
    /// </remarks>
    private static bool IsBoolean(string value) => bool.TryParse(value, out _);

    private static string Cap(string value) =>
        value.Length <= MaximumValueLength
            ? value
            : string.Concat(value.AsSpan(0, MaximumValueLength), Truncated);
}
