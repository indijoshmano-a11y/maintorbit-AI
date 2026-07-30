namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Cross-origin settings for the management API.
/// </summary>
/// <remarks>
/// Specification §3.8 and <c>docs/05-security/security-architecture.md</c> §24.
/// <para>
/// These apply to the management API, which the web console calls from a browser. The AI
/// Gateway does <b>not</b> permit browser origins at all — permitting them would invite
/// customers to embed a Platform API Key in client-side JavaScript, where anyone can read
/// it. That is a deliberate policy, not an oversight, and the Gateway is configured
/// separately.
/// </para>
/// </remarks>
public sealed class CorsOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Cors";

    /// <summary>
    /// Exact origins permitted to call the management API from a browser.
    /// </summary>
    /// <remarks>
    /// An explicit allowlist — never a wildcard. Matching is exact; prefix or wildcard
    /// matching is how an attacker-controlled subdomain gets accepted.
    /// </remarks>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>
    /// Whether browsers may send credentials — cookies and the Authorization header —
    /// on cross-origin requests.
    /// </summary>
    /// <remarks>
    /// Required for the console, which carries its refresh token in an <c>HttpOnly</c>
    /// cookie. Enabling this with a wildcard origin is rejected at startup by
    /// <see cref="CorsOptionsValidator"/>.
    /// </remarks>
    public bool AllowCredentials { get; init; }

    /// <summary>
    /// HTTP methods permitted cross-origin. Explicit rather than inferred.
    /// </summary>
    public string[] AllowedMethods { get; init; } = [];

    /// <summary>
    /// Request headers permitted cross-origin. Explicit rather than inferred.
    /// </summary>
    public string[] AllowedHeaders { get; init; } = [];
}
