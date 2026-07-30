using System.ComponentModel.DataAnnotations;

namespace MaintOrbit.Api.Configuration;

/// <summary>
/// Which upstream proxies this host will accept forwarded headers from.
/// </summary>
/// <remarks>
/// The deployment terminates TLS at Nginx and forwards to the API upstream
/// (deployment-architecture §3.5). The application therefore never sees the client's real
/// scheme or address directly — it sees the proxy's — unless it reads <c>X-Forwarded-*</c>.
/// <para>
/// Those headers are supplied by whoever connected. Honouring them from an arbitrary source
/// lets a caller state any client address it likes, which would forge the input that
/// IP-based rate limiting and audit records are built on. They are consequently trusted only
/// from proxies named here, and the trust is opt-in per environment.
/// </para>
/// </remarks>
public sealed class ReverseProxyOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// Whether this host runs behind a trusted reverse proxy.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. A host that is not behind a proxy but processes
    /// forwarded headers anyway is directly spoofable, so the unsafe direction is the one
    /// that requires a deliberate act.
    /// </remarks>
    public bool Enabled { get; init; }

    /// <summary>
    /// Individual proxy addresses whose forwarded headers are honoured.
    /// </summary>
    public IReadOnlyList<string> KnownProxies { get; init; } = [];

    /// <summary>
    /// Proxy networks, in CIDR notation, whose forwarded headers are honoured.
    /// </summary>
    /// <remarks>
    /// Usually the more practical of the two: a container network assigns the proxy an
    /// address that changes between deployments, so pinning the network survives a restart
    /// where pinning the address does not.
    /// </remarks>
    public IReadOnlyList<string> KnownNetworks { get; init; } = [];

    /// <summary>
    /// How many proxy hops to walk back through the forwarded chain.
    /// </summary>
    /// <remarks>
    /// One, matching the single Nginx hop in the documented topology. The value is the
    /// number of entries taken from the <i>right</i> of the chain — the segments a trusted
    /// proxy appended. Raising it past the real hop count starts consuming entries the
    /// client supplied, which is the point at which the client chooses its own address.
    /// </remarks>
    [Range(1, 8)]
    public int ForwardLimit { get; init; } = 1;
}
