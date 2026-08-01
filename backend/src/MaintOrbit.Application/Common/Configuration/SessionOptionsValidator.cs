using Microsoft.Extensions.Options;

namespace MaintOrbit.Application.Common.Configuration;

/// <summary>Rejects session lifetimes that cannot both hold.</summary>
public sealed class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
    public ValidateOptionsResult Validate(string? name, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.IdleTimeoutMinutes >= options.AbsoluteLifetimeMinutes)
        {
            // An idle window at or beyond the absolute lifetime can never expire a session first,
            // so the idle timeout would exist in configuration and do nothing — the kind of
            // control that is assumed to be working precisely because nobody sees it fail.
            return ValidateOptionsResult.Fail(
                $"Sessions:IdleTimeoutMinutes ({options.IdleTimeoutMinutes}) must be shorter than " +
                $"Sessions:AbsoluteLifetimeMinutes ({options.AbsoluteLifetimeMinutes}); " +
                "otherwise the idle timeout can never take effect.");
        }

        return ValidateOptionsResult.Success;
    }
}
