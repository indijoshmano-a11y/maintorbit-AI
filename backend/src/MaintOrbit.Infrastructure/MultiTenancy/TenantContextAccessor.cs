using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Infrastructure.MultiTenancy;

/// <summary>
/// Ambient tenant context backed by <see cref="AsyncLocal{T}"/>.
/// </summary>
/// <remarks>
/// The same mechanism as the correlation accessor, for the same reason: the value must survive
/// every <c>await</c> and thread-pool hop without being threaded through method signatures. A
/// tenant passed as a parameter is a tenant somebody eventually forgets to pass, and a query
/// issued without it is the failure this whole design exists to prevent.
/// <para>
/// The backing field is <see langword="static"/>, so a singleton registration is correct: the
/// instance holds no state, and the value belongs to the caller's execution context. A scoped
/// registration would also leave the Worker — which has no request scope — unable to establish
/// context at all, which TC-5 requires it to do.
/// </para>
/// </remarks>
internal sealed class TenantContextAccessor : ITenantContext
{
    private static readonly AsyncLocal<CompanyId?> Ambient = new();

    /// <inheritdoc />
    public CompanyId? Current => Ambient.Value;

    /// <inheritdoc />
    public CompanyId Require() =>
        Ambient.Value
        ?? throw new InvalidOperationException(
            "No Company is in scope. Tenant context is resolved server-side from the credential " +
            "(TC-1) and a request must never proceed untenanted (TC-3). Background work " +
            "establishes it explicitly from the job payload (TC-5).");

    /// <inheritdoc />
    public IDisposable BeginTenantScope(CompanyId companyId)
    {
        if (companyId.IsEmpty)
        {
            // An empty discriminator would be written into the session variable and match no
            // rows, which is safe but indistinguishable from a genuine empty result. Rejecting
            // it here keeps "no tenant" and "tenant with no data" from looking the same.
            throw new ArgumentException(
                "A tenant scope requires a Company.", nameof(companyId));
        }

        var previous = Ambient.Value;
        Ambient.Value = companyId;

        return new AmbientScope(previous);
    }

    private sealed class AmbientScope(CompanyId? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
