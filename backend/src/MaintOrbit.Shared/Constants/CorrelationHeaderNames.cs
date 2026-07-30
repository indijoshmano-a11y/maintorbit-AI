namespace MaintOrbit.Shared.Constants;

/// <summary>
/// Header names carrying correlation across process boundaries.
/// </summary>
/// <remarks>
/// Defined in the shared kernel rather than in the API host because correlation crosses more
/// than one boundary. The API reads this header inbound and returns it (api-specification
/// §4.2 — <b>every response</b>), and the outbound provider clients in the infrastructure
/// layer must forward it so that NFR-OBS-004's "full request path including provider calls"
/// remains connected. A constant in the host would be unreachable from the layer that needs
/// it most.
/// </remarks>
public static class CorrelationHeaderNames
{
    /// <summary>
    /// Correlation identifier header, inbound and outbound.
    /// </summary>
    public const string CorrelationId = "X-Correlation-Id";
}
