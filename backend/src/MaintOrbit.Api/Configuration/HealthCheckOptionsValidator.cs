using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Cross-property validation for <see cref="HealthCheckOptions"/>.
/// </summary>
public sealed class HealthCheckOptionsValidator : IValidateOptions<HealthCheckOptions>
{
    public ValidateOptionsResult Validate(string? name, HealthCheckOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.Equals(options.LivenessPath, options.ReadinessPath, StringComparison.OrdinalIgnoreCase))
        {
            // Collapsing the two defeats NFR-OBS-005. If readiness answers liveness, a host
            // whose dependencies are unreachable reports healthy and receives traffic.
            return ValidateOptionsResult.Fail(
                $"{HealthCheckOptions.SectionName}: LivenessPath and ReadinessPath must differ. " +
                "NFR-OBS-005 requires liveness and readiness to be distinguishable.");
        }

        return ValidateOptionsResult.Success;
    }
}
