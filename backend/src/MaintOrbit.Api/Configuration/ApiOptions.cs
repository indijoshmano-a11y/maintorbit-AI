using System.ComponentModel.DataAnnotations;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Management API surface settings.
/// </summary>
/// <remarks>
/// Every value here is specified in <c>docs/07-api/api-specification.md</c>. The defaults
/// are the documented defaults, so an unconfigured deployment behaves as the specification
/// describes rather than as whatever the framework happens to do.
/// <para>
/// These settings govern the management API only. The AI Gateway is a separate surface on
/// its own base path with its own authentication and limits (§1.1), and is configured
/// separately when that milestone lands.
/// </para>
/// </remarks>
public sealed class ApiOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Api";

    /// <summary>
    /// Base path for the versioned management API. Specification §1.4.
    /// </summary>
    /// <remarks>
    /// URL-segment versioning was chosen so the version is visible in every log line,
    /// support request, and reverse-proxy rule.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^/[a-z0-9\-/]+$", ErrorMessage = "BasePath must be a lowercase absolute path, for example /api/v1.")]
    public string BasePath { get; init; } = "/api/v1";

    /// <summary>
    /// Page size applied when a request does not specify one. Specification §5.5.
    /// </summary>
    [Range(1, 200)]
    public int DefaultPageSize { get; init; } = 50;

    /// <summary>
    /// Largest page size a client may request. Specification §5.5.
    /// </summary>
    [Range(1, 1000)]
    public int MaxPageSize { get; init; } = 200;

    /// <summary>
    /// Longest time range permitted on a single ledger or audit query, in days.
    /// Specification §5.5.
    /// </summary>
    /// <remarks>
    /// Ledger and audit collections require a time-range filter and cap its span. An
    /// unbounded query across a partitioned table holding hundreds of millions of rows is
    /// not a query anyone intends to execute.
    /// </remarks>
    [Range(1, 366)]
    public int MaxQueryRangeDays { get; init; } = 90;

    /// <summary>
    /// Largest number of values accepted for a single repeated filter parameter.
    /// Specification §5.5.
    /// </summary>
    [Range(1, 100)]
    public int MaxFilterValuesPerParameter { get; init; } = 20;
}
