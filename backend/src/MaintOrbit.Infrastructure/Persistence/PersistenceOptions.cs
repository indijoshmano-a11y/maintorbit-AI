using System.ComponentModel.DataAnnotations;

namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// Database connection settings.
/// </summary>
/// <remarks>
/// Deliberately small, and one omission is deliberate: <b>there is no pooling mode setting.</b>
/// Pooling mode is unresolved (DD-2) and <c>docs/06-database/database-design.md</c> §5 records
/// that it <i>blocks implementation</i>. §6.7 explains why it is a security decision rather
/// than a performance one — a pooled connection returned with the tenant session variable
/// still set, then handed to a request for a different Company, is a cross-tenant exposure
/// that presents as an ordinary successful query. Choosing a mode here would be choosing that
/// on someone else's behalf.
/// </remarks>
public sealed class PersistenceOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Persistence";

    /// <summary>
    /// Npgsql connection string.
    /// </summary>
    /// <remarks>
    /// Carries a credential in every real deployment, so it belongs in the environment or in
    /// git-ignored local settings — never in a committed file (CF-3).
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Connection used by the one path that reads across Companies.
    /// </summary>
    /// <remarks>
    /// Optional, and points at a role permitted to see past row-level security. Only
    /// <c>ICredentialDirectory</c> uses it, and only to resolve which Company a credential belongs
    /// to — authentication cannot open a tenant scope before it knows the tenant.
    /// <para>
    /// When absent the ordinary connection is used. That is correct in development, where the
    /// developer's own role already sees everything, and fail-closed in production: a properly
    /// restricted application role returns no rows, so sign-in stops working visibly rather than
    /// authenticating against the wrong tenant.
    /// </para>
    /// </remarks>
    public string? ElevatedConnectionString { get; init; }

    /// <summary>
    /// How long a single command may run before it is cancelled.
    /// </summary>
    /// <remarks>
    /// Bounded above because an unbounded command holds a connection, and a connection held is
    /// one the rest of the system cannot have. The ceiling is well below the Nginx timeout so
    /// that the application, not the proxy, decides how a slow query ends
    /// (deployment-architecture §3.5).
    /// </remarks>
    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Transient-failure retry attempts. Zero disables retrying.
    /// </summary>
    /// <remarks>
    /// Defaults to disabled, and that default is a position rather than an oversight. EF's
    /// retrying execution strategy re-runs a failed operation, which can place it on a
    /// different connection. Under session-scoped row-level security the tenant variable is set
    /// on the connection the operation started on, so a retry that lands elsewhere would run
    /// without tenant context — the exact failure DD-2 exists to rule out. Enable it once
    /// DD-2 is settled and the interaction has been tested.
    /// </remarks>
    [Range(0, 10)]
    public int MaxRetryAttempts { get; init; }

    /// <summary>
    /// Upper bound on the delay between retry attempts.
    /// </summary>
    [Range(1, 120)]
    public int MaxRetryDelaySeconds { get; init; } = 10;
}
