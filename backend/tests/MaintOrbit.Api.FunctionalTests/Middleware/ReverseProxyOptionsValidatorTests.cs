using MaintOrbit.Api.Configuration;

namespace MaintOrbit.Api.FunctionalTests.Middleware;

/// <summary>
/// Covers the reverse proxy trust settings.
/// </summary>
/// <remarks>
/// Every case here fails silently at runtime if it is not caught at startup — forwarded
/// headers are either discarded or honoured from the wrong source, and both look like a
/// working system.
/// </remarks>
public sealed class ReverseProxyOptionsValidatorTests
{
    private static readonly ReverseProxyOptionsValidator Validator = new();

    [Fact]
    public void Disabled_NeedsNoProxyList()
    {
        var result = Validator.Validate(null, new ReverseProxyOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Enabled_WithoutAnyProxy_IsRejected()
    {
        // The framework's default trust list is loopback only. In a container deployment that
        // means the headers are dropped and every request appears to come from Nginx.
        var result = Validator.Validate(null, new ReverseProxyOptions { Enabled = true });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Enabled_WithAKnownNetwork_IsAccepted()
    {
        var result = Validator.Validate(null, new ReverseProxyOptions
        {
            Enabled = true,
            KnownNetworks = ["172.18.0.0/16"]
        });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void NetworkTrustingEveryAddress_IsRejected(string everything)
    {
        // Trusting the whole internet lets any caller assert its own client address, which
        // defeats IP-based rate limiting and writes attacker-chosen values into audit records.
        var result = Validator.Validate(null, new ReverseProxyOptions
        {
            Enabled = true,
            KnownNetworks = [everything]
        });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("172.18.0.0")]
    [InlineData("not-an-address/16")]
    [InlineData("172.18.0.0/64")]
    public void MalformedNetwork_IsRejected(string malformed)
    {
        var result = Validator.Validate(null, new ReverseProxyOptions
        {
            Enabled = true,
            KnownNetworks = [malformed]
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void MalformedProxyAddress_IsRejected()
    {
        var result = Validator.Validate(null, new ReverseProxyOptions
        {
            Enabled = true,
            KnownProxies = ["nginx"]
        });

        Assert.True(result.Failed);
    }
}
