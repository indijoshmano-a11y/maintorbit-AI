using System.ComponentModel.DataAnnotations;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Application-wide identity settings.
/// </summary>
/// <remarks>
/// Deliberately small. Properties are added as the milestone that needs them lands,
/// following the same principle as <c>Directory.Packages.props</c> — configuration is
/// declared when it is used, not in advance. Speculative settings become stale and
/// nobody dares delete them.
/// </remarks>
public sealed class ApplicationOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Application";

    /// <summary>
    /// Product name, used wherever the application identifies itself.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Absolute public base URL of this deployment.
    /// </summary>
    /// <remarks>
    /// Required because several capabilities need to generate absolute URLs that a
    /// customer will follow — verification links, invitation links, and the OAuth2
    /// redirect allowlist. Deriving these from the inbound request is the usual shortcut
    /// and is unsafe behind a reverse proxy, where the Host header is attacker-influenced.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string PublicBaseUrl { get; init; } = string.Empty;
}
