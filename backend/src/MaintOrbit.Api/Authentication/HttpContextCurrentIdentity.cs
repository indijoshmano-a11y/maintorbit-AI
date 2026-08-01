using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Api.Authentication;

/// <summary>
/// Reads the current identity from the request's validated principal.
/// </summary>
/// <remarks>
/// Backed by <see cref="IHttpContextAccessor"/>, so it reports nothing outside a request — which
/// is correct for startup, health probes, and the Worker rather than an omission. Registered as a
/// singleton because <see cref="IHttpContextAccessor"/> is itself an ambient accessor and holds no
/// state of its own.
/// <para>
/// The principal it reads has already passed signature, issuer, audience, lifetime, algorithm, and
/// token-type validation, and its session has been confirmed live. Nothing here re-checks any of
/// that; this only reads what the pipeline already established.
/// </para>
/// </remarks>
internal sealed class HttpContextCurrentIdentity(IHttpContextAccessor accessor) : ICurrentIdentity
{
    /// <inheritdoc />
    public bool IsAuthenticated =>
        accessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public EmployeeId? EmployeeId =>
        IsAuthenticated ? accessor.HttpContext!.User.GetEmployeeId() : null;

    /// <inheritdoc />
    public CompanyId? CompanyId =>
        IsAuthenticated ? accessor.HttpContext!.User.GetCompanyId() : null;

    /// <inheritdoc />
    public SessionId? SessionId =>
        IsAuthenticated ? accessor.HttpContext!.User.GetSessionId() : null;

    /// <inheritdoc />
    public EmployeeId RequireEmployeeId() => EmployeeId ?? throw NotAuthenticated();

    /// <inheritdoc />
    public CompanyId RequireCompanyId() => CompanyId ?? throw NotAuthenticated();

    /// <inheritdoc />
    public SessionId RequireSessionId() => SessionId ?? throw NotAuthenticated();

    private static InvalidOperationException NotAuthenticated() =>
        new("The current request is not authenticated. " +
            "Identity is established from a validated access token; background work and " +
            "unauthenticated endpoints have none.");
}
