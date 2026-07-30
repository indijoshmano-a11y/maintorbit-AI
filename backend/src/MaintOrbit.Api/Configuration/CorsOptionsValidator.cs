using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Cross-property validation for <see cref="CorsOptions"/>.
/// </summary>
/// <remarks>
/// DataAnnotations validate a property in isolation. These rules span properties, and the
/// first is a security rule rather than a correctness one: specification §3.8 states
/// <b>never a wildcard with credentials</b>. A browser that is told both "any origin may
/// call this" and "send the cookies" will do exactly that, which hands the console's
/// session to any site the user visits.
/// <para>
/// Catching this at startup rather than in review matters because the mistake is a single
/// character in a configuration file, made by whoever is trying to unblock themselves at
/// the time.
/// </para>
/// </remarks>
public sealed class CorsOptionsValidator : IValidateOptions<CorsOptions>
{
    public ValidateOptionsResult Validate(string? name, CorsOptions options)
    {
        List<string> failures = [];

        var hasWildcard = options.AllowedOrigins.Any(origin => origin is "*");

        if (options.AllowCredentials && hasWildcard)
        {
            failures.Add(
                $"{CorsOptions.SectionName}: AllowCredentials cannot be combined with a wildcard origin. " +
                "Specify exact origins. See docs/07-api/api-specification.md §3.8.");
        }

        if (options.AllowCredentials && options.AllowedOrigins.Length == 0)
        {
            failures.Add(
                $"{CorsOptions.SectionName}: AllowCredentials is enabled but AllowedOrigins is empty. " +
                "Credentialed cross-origin requests require an explicit allowlist.");
        }

        foreach (var origin in options.AllowedOrigins.Where(o => o is not "*"))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                failures.Add($"{CorsOptions.SectionName}: '{origin}' is not an absolute URI.");
                continue;
            }

            if (uri.Scheme is not ("http" or "https"))
            {
                failures.Add($"{CorsOptions.SectionName}: '{origin}' must use http or https.");
            }

            // A browser's Origin header carries no path and no trailing slash. An entry
            // with either never matches, and the resulting failure looks like a CORS bug
            // rather than a configuration typo.
            if (origin.EndsWith('/') || uri.AbsolutePath is not "/")
            {
                failures.Add(
                    $"{CorsOptions.SectionName}: '{origin}' must be scheme and host only, with no path or trailing slash.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
