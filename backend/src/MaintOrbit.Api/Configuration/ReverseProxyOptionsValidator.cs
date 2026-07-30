using System.Net;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Rejects reverse proxy settings that would make forwarded headers untrustworthy.
/// </summary>
/// <remarks>
/// Every failure here is one that produces no error at runtime. A misconfigured proxy list
/// either silently discards the headers — leaving every client apparently connecting from
/// the proxy — or silently accepts them from anyone. Both look like a working system, so
/// they are caught at startup instead (ADR-0021: security controls fail closed).
/// </remarks>
public sealed class ReverseProxyOptionsValidator : IValidateOptions<ReverseProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ReverseProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            // Not behind a proxy. Nothing downstream reads forwarded headers, so the address
            // lists are irrelevant and are not worth failing a boot over.
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (options.KnownProxies.Count == 0 && options.KnownNetworks.Count == 0)
        {
            // The framework's default trust list covers loopback only. In a container
            // deployment the proxy is a different host, so leaving this empty means the
            // headers are quietly ignored and every request appears to originate from Nginx.
            failures.Add(
                $"{ReverseProxyOptions.SectionName} is enabled but names no proxy. " +
                "Set KnownProxies or KnownNetworks, otherwise forwarded headers are discarded " +
                "and every client appears to connect from the proxy.");
        }

        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                failures.Add($"KnownProxies contains '{proxy}', which is not an IP address.");
            }
        }

        foreach (var network in options.KnownNetworks)
        {
            ValidateNetwork(network, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateNetwork(string network, List<string> failures)
    {
        // IPNetwork.TryParse is the same parser the forwarded headers middleware uses, so a
        // value accepted here is one the middleware will accept too. It also rejects a prefix
        // with host bits set — 172.18.0.5/16 rather than 172.18.0.0/16 — which is an easy
        // thing to write and would otherwise throw at startup from inside the options
        // callback, well away from the setting that caused it.
        if (!IPNetwork.TryParse(network, out var parsed))
        {
            failures.Add(
                $"KnownNetworks contains '{network}', which is not a valid CIDR range. " +
                "Use a network address and prefix length, such as 172.18.0.0/16.");
            return;
        }

        if (parsed.PrefixLength == 0)
        {
            // A zero-length prefix trusts every address on the internet. Any caller could then
            // set X-Forwarded-For to whatever it wanted, which defeats IP-based rate limiting
            // and writes attacker-chosen addresses into audit records.
            failures.Add(
                $"KnownNetworks contains '{network}', which trusts every address. " +
                "Name the proxy network explicitly.");
        }
    }
}
