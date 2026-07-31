namespace MaintOrbit.Shared.MultiTenancy;

/// <summary>
/// The Company whose data the current operation is permitted to see.
/// </summary>
/// <remarks>
/// TC-1 is the rule this exists to make enforceable: the tenant is <b>derived server-side from
/// the credential</b>, never from a request parameter, header, or body. Nothing here accepts a
/// tenant from transport — the scope is opened by whatever resolved the credential, and every
/// component downstream reads rather than sets.
/// <para>
/// TC-2 requires it resolved once at ingress into an ambient scoped context, and TC-5 requires
/// background jobs to establish it explicitly from the job payload. Both are the same shape:
/// open a scope, run the work inside it. That is why this is not tied to a request.
/// </para>
/// <para>
/// Lives in the shared kernel alongside <see cref="CompanyId"/> because every layer that touches
/// tenant-scoped data needs it, including the ones that must not know a port exists.
/// </para>
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// The Company in scope, or <see langword="null"/> outside a tenant-scoped operation.
    /// </summary>
    /// <remarks>
    /// Nullable because untenanted work genuinely exists — startup, health probes, and the
    /// enumerated elevated paths of TC-6. Reporting a fabricated Company for those would be
    /// worse than reporting none.
    /// </remarks>
    CompanyId? Current { get; }

    /// <summary>
    /// The Company in scope, or a failure if there is none.
    /// </summary>
    /// <exception cref="InvalidOperationException">No tenant scope is open.</exception>
    /// <remarks>
    /// TC-3: a request never proceeds untenanted. Callers that require a tenant use this so the
    /// fail-closed path is a single call rather than a null check each site would have to
    /// remember — and forgetting it is what produces an untenanted query.
    /// </remarks>
    CompanyId Require();

    /// <summary>
    /// Runs the enclosing operation as <paramref name="companyId"/> until disposed.
    /// </summary>
    /// <remarks>
    /// The previous value is restored on dispose rather than cleared, so a nested operation
    /// returns its caller to the tenant it started with. Nesting is expected: the elevated paths
    /// of TC-6 process one Company at a time inside an untenanted outer scope.
    /// </remarks>
    IDisposable BeginTenantScope(CompanyId companyId);
}
