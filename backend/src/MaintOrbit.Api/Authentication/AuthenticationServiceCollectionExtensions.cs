using System.Text.Json;
using MaintOrbit.Api.Authorization;
using MaintOrbit.Application.Abstractions.Security;
using Microsoft.AspNetCore.Authorization;
using MaintOrbit.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Authentication;

/// <summary>
/// Registers JWT bearer authentication.
/// </summary>
/// <remarks>
/// The handler is configured from <see cref="AccessTokenValidationParameters"/> — the same
/// definition <c>IAccessTokenValidator</c> uses — so the middleware and the port cannot come to
/// different conclusions about the same token. A second, hand-written set of parameters here would
/// be a security control that drifts silently: the dangerous direction is the middleware accepting
/// something the validator would refuse, which produces no error anywhere.
/// </remarks>
public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>camelCase and no nulls on the wire, matching the documented envelope (§1.6, §4.3).</summary>
    internal static readonly JsonSerializerOptions ProblemJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Registers bearer authentication and the current-identity accessor.</summary>
    public static IServiceCollection AddJwtBearerAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Required by the current-identity accessor. Registered here rather than assumed, because
        // its absence is a null reference at the first authenticated request.
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ICurrentIdentity, HttpContextCurrentIdentity>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(ConfigureBearer);

        // Authorization is endpoint-based, so its policy cache resolves EndpointDataSource.
        // Added here rather than assumed: the web host registers routing implicitly, but a
        // composition root built by hand does not, and the failure is a container that will not
        // build rather than anything visible at the call site.
        services.AddRouting();

        // Wired so [Authorize] resolves. Still no policy registered by name: endpoints declare a
        // permission and PermissionPolicyProvider derives the policy from it, so there is no
        // startup list to keep in step with the endpoints it serves.
        services.AddAuthorization();

        // Derives a policy from any permission an endpoint declares, so declaring one is all there
        // is to do — no startup list to keep in step with the endpoints it serves.
        //
        // AddSingleton, not TryAddSingleton. AddAuthorization above has already registered
        // DefaultAuthorizationPolicyProvider against this service type, so TryAdd sees it as
        // present and silently does nothing — leaving every permission policy unresolvable. The
        // symptom is not a denial but an InvalidOperationException at the first protected request:
        // "the AuthorizationPolicy named ... was not found". Registering last wins for a single
        // resolve, which is what the middleware does.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

        // TryAddEnumerable, not TryAddScoped. IAuthorizationHandler is a multi-implementation
        // service — AddAuthorization registers PassThroughAuthorizationHandler against it — and
        // TryAdd matches on the service type alone, so it would find one present and add nothing.
        // The enumerable form deduplicates on the implementation type instead, which is the
        // question actually being asked.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAuthorizationHandler, PermissionAuthorizationHandler>());

        return services;
    }

    private static void ConfigureBearer(JwtBearerOptions bearer)
    {
        // Nothing may be inferred from an unauthenticated metadata endpoint. The keys are
        // configured locally, so there is no discovery document to fetch and no network
        // dependency on the authentication path.
        bearer.RequireHttpsMetadata = true;
        bearer.SaveToken = false;

        // Keep the claim names the token actually carries. The default rewrites registered claims
        // to legacy WS-Federation URIs — `sub` becomes a schemas.xmlsoap.org identifier — so a
        // reader looking for the documented name finds nothing and the request is refused as
        // incomplete. Both ends of this token are ours; the compatibility mapping only obscures it.
        bearer.MapInboundClaims = false;

        bearer.Events = new JwtBearerEvents
        {
            OnTokenValidated = ValidateTokenTypeAsync,
            OnChallenge = WriteUniformChallengeAsync,
            OnForbidden = WriteForbiddenAsync
        };
    }

    /// <summary>
    /// Applies the parameters once options are available.
    /// </summary>
    /// <remarks>
    /// A post-configure step rather than a constructor argument, because the bearer options are
    /// built by the authentication framework and cannot take a dependency directly.
    /// </remarks>
    internal sealed class ConfigureJwtBearer(IAccessTokenValidationParametersFactory parameters)
        : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

        public void Configure(string? name, JwtBearerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (name is not JwtBearerDefaults.AuthenticationScheme)
            {
                return;
            }

            options.TokenValidationParameters = parameters.Create();
        }
    }

    /// <summary>
    /// Writes the documented envelope for an authenticated caller who is not permitted.
    /// </summary>
    /// <remarks>
    /// The framework's default is a bare <c>403</c> with an empty body, which §4.3 does not allow
    /// — every error carries the envelope, and a client that gets nothing back cannot tell a
    /// refusal from a broken response. §7 maps this to <c>permission_denied</c>.
    /// <para>
    /// <b>It names no permission.</b> Reporting which one was missing would turn a denial into a
    /// map of the permission model, letting a caller enumerate the catalogue by walking endpoints
    /// and reading what each one asked for. The caller already knows what they tried to do; what
    /// they may not learn is what would have been enough.
    /// </para>
    /// <para>
    /// FR-PERM-004 requires every denial to produce an audit event. The auditing module does not
    /// exist, so that is outstanding rather than done — recorded here because this is the line it
    /// belongs on.
    /// </para>
    /// </remarks>
    private static async Task WriteForbiddenAsync(ForbiddenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Type = "permission_denied",
            Title = "Not permitted",
            Status = StatusCodes.Status403Forbidden,
            Detail = "You do not have permission to perform this operation."
        };

        problem.Extensions["correlationId"] =
            context.HttpContext.RequestServices
                .GetService<MaintOrbit.Shared.Abstractions.ICorrelationIdAccessor>()?.Current;

        // Retrying changes nothing until somebody grants the permission, which is not the
        // caller's to do.
        problem.Extensions["retryable"] = false;

        await JsonSerializer
            .SerializeAsync(
                context.Response.Body,
                problem,
                ProblemJson,
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects a token that is not an access token.
    /// </summary>
    /// <remarks>
    /// SD-013: "token type is a validated claim, not a convention. A refresh token presented as an
    /// access token must be rejected" — a real and commonly-missed confusion attack. The bearer
    /// handler validates the JWT itself and knows nothing about this claim, so it is checked here,
    /// before anything downstream treats the principal as authenticated.
    /// </remarks>
    private static Task ValidateTokenTypeAsync(TokenValidatedContext context)
    {
        var tokenType = context.Principal?.FindFirst(AccessTokenClaimNames.TokenType)?.Value;

        if (!string.Equals(tokenType, AccessTokenTypes.Access, StringComparison.Ordinal))
        {
            context.Fail("Unsupported token type.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the documented error envelope and suppresses the handler's own reason.
    /// </summary>
    /// <remarks>
    /// The default challenge emits <c>WWW-Authenticate: Bearer error="invalid_token",
    /// error_description="The token expired at ..."</c> — which tells an attacker exactly which
    /// part of a forged token to fix next, and leaks a timestamp belonging to someone else's
    /// session. The header is reduced to the bare scheme and the body is the envelope §4.3
    /// defines.
    /// </remarks>
    private static async Task WriteUniformChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        // The scheme only. No error code, no description.
        context.Response.Headers.WWWAuthenticate = "Bearer";

        var problem = new ProblemDetails
        {
            Type = "authentication_failed",
            Title = "Authentication failed",
            Status = StatusCodes.Status401Unauthorized,
            Detail = "A valid access token is required."
        };

        problem.Extensions["correlationId"] =
            context.HttpContext.RequestServices
                .GetService<MaintOrbit.Shared.Abstractions.ICorrelationIdAccessor>()?.Current;
        problem.Extensions["retryable"] = false;

        await JsonSerializer
            .SerializeAsync(
                context.Response.Body,
                problem,
                ProblemJson,
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
    }
}
