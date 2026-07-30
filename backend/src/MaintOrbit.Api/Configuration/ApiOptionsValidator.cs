using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Cross-property validation for <see cref="ApiOptions"/>.
/// </summary>
public sealed class ApiOptionsValidator : IValidateOptions<ApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiOptions options)
    {
        List<string> failures = [];

        if (options.DefaultPageSize > options.MaxPageSize)
        {
            failures.Add(
                $"{ApiOptions.SectionName}: DefaultPageSize ({options.DefaultPageSize}) exceeds " +
                $"MaxPageSize ({options.MaxPageSize}). Every unqualified request would be rejected.");
        }

        if (options.BasePath.EndsWith('/'))
        {
            // Route composition appends a leading-slash segment. A trailing slash here
            // produces '/api/v1//employees', which does not match.
            failures.Add($"{ApiOptions.SectionName}: BasePath must not end with '/'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
