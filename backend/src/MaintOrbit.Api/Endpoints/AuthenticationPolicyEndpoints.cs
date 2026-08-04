using System.ComponentModel.DataAnnotations;
using MaintOrbit.Api.Authorization;
using MaintOrbit.Api.Configuration;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.Modules.Identity.Commands.AuthenticationPolicy;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Api.Endpoints;

/// <summary>
/// The Company's authentication policy — <c>/api/v1/company/authentication-policy</c>.
/// </summary>
/// <remarks>
/// api-specification §3.3 makes the Company path <b>singular</b> — "a caller has exactly one
/// Company" — so neither endpoint takes an identifier, and TC-1 supplies the tenant from the
/// credential. §3.7 assigns <c>company.manage [C]</c> to Company settings, and §3.10 of the
/// authentication architecture is what requires this policy to exist at MVP.
/// <para>
/// <b>Reading is not the same permission as changing.</b> Both hold <c>company.manage [C]</c>
/// here, because the policy states how weak a password may be and how long a session lives —
/// knowing that is itself reconnaissance, and §3.7 gives settings one permission rather than a
/// read/write pair.
/// </para>
/// <para>
/// <b>Step-up authentication is outstanding.</b> §3.7 requires it for changes to authentication
/// policy — disabling MFA for everyone is exactly the operation a hijacked session would perform.
/// Enforcement does not exist yet; this endpoint holds a permission and no more, and that gap is
/// recorded rather than papered over.
/// </para>
/// </remarks>
public static class AuthenticationPolicyEndpoints
{
    /// <summary>Maps the authentication policy endpoints.</summary>
    public static IEndpointRouteBuilder MapAuthenticationPolicyEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var basePath = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ApiOptions>>().Value.BasePath;

        var group = endpoints.MapGroup($"{basePath}/company/authentication-policy");

        group.MapGet("/", GetAsync).RequirePermission(IdentityPermissions.CompanyManage);
        group.MapPut("/", UpdateAsync).RequirePermission(IdentityPermissions.CompanyManage);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        IQueryHandler<GetAuthenticationPolicyQuery, AuthenticationPolicyView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new GetAuthenticationPolicyQuery(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.Ok(Map(result.Value)) : Problem(context, result.Error);
    }

    /// <summary>
    /// Replaces the whole policy.
    /// </summary>
    /// <remarks>
    /// <c>PUT</c> rather than <c>PATCH</c>, and §4.1's table returns <c>200</c> with the updated
    /// resource. The rules are relational, so a partial update would refuse a legitimate pair
    /// depending on which half arrived first.
    /// </remarks>
    private static async Task<IResult> UpdateAsync(
        AuthenticationPolicyRequest request,
        HttpContext context,
        ICommandHandler<UpdateAuthenticationPolicyCommand, AuthenticationPolicyView> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler.HandleAsync(
            new UpdateAuthenticationPolicyCommand(
                request.MinimumPasswordLength,
                request.RequireBreachCheck,
                request.IdleTimeoutMinutes,
                request.AbsoluteLifetimeMinutes,
                request.MfaRequired,
                request.MaximumFailedAttempts,
                request.LockoutMinutes),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Results.Ok(Map(result.Value)) : Problem(context, result.Error);
    }

    private static AuthenticationPolicyResponse Map(AuthenticationPolicyView view) =>
        new(view.MinimumPasswordLength,
            view.RequireBreachCheck,
            view.IdleTimeoutMinutes,
            view.AbsoluteLifetimeMinutes,
            view.MfaRequired,
            view.MaximumFailedAttempts,
            view.LockoutMinutes,
            view.IsCompanyConfigured);

    /// <summary>Runs DataAnnotations and returns the documented validation envelope (§4.5).</summary>
    /// <remarks>
    /// Shape only. Whether the numbers make a coherent policy is the aggregate's to say, and
    /// splitting that judgement across two layers is how the two come to disagree.
    /// </remarks>
    private static IResult? Validate<TRequest>(TRequest request)
        where TRequest : notnull
    {
        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(request, context, results, validateAllProperties: true))
        {
            return null;
        }

        var problem = new ProblemDetails
        {
            Type = "validation_failed",
            Title = "The request is not valid",
            Status = StatusCodes.Status400BadRequest,
            Detail = "One or more fields are missing or malformed."
        };

        problem.Extensions["errors"] = results
            .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty),
                (result, member) => new
                {
                    field = ToCamelCase(member),
                    code = "out_of_range",
                    message = result.ErrorMessage
                })
            .ToArray();

        problem.Extensions["retryable"] = false;

        return Results.Json(
            problem,
            Authentication.AuthenticationServiceCollectionExtensions.ProblemJson,
            contentType: "application/problem+json",
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Writes the documented error envelope (§4.3).</summary>
    private static IResult Problem(HttpContext context, Error error)
    {
        var problem = new ProblemDetails
        {
            Type = error.Code,
            Title = "The request is not valid",
            Status = StatusCodes.Status400BadRequest,
            Detail = error.Description
        };

        problem.Extensions["correlationId"] = context.RequestServices
            .GetService<Shared.Abstractions.ICorrelationIdAccessor>()?.Current;
        problem.Extensions["retryable"] = false;

        return Results.Json(
            problem,
            Authentication.AuthenticationServiceCollectionExtensions.ProblemJson,
            contentType: "application/problem+json",
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

/// <summary>A replacement authentication policy.</summary>
/// <remarks>
/// The ranges mirror the aggregate's constants so a caller gets a field-level error rather than
/// one sentence about the whole policy. The aggregate still checks every one of them — this is the
/// shape check, not the rule.
/// </remarks>
public sealed record AuthenticationPolicyRequest
{
    /// <summary>Shortest password the Company accepts (FR-AUTH-002).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumAllowedPasswordLength,
        CompanyAuthenticationPolicy.MaximumAllowedPasswordLength)]
    public int MinimumPasswordLength { get; init; }

    /// <summary>Whether new passwords are checked against breach corpora (FR-AUTH-002).</summary>
    public bool RequireBreachCheck { get; init; }

    /// <summary>Idle window in minutes (FR-AUTH-007).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumIdleTimeoutMinutes,
        CompanyAuthenticationPolicy.MaximumIdleTimeoutMinutes)]
    public int IdleTimeoutMinutes { get; init; }

    /// <summary>Absolute lifetime in minutes (FR-AUTH-007).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumAbsoluteLifetimeMinutes,
        CompanyAuthenticationPolicy.MaximumAbsoluteLifetimeMinutes)]
    public int AbsoluteLifetimeMinutes { get; init; }

    /// <summary>Whether every Employee must hold a second factor (FR-AUTH-006).</summary>
    public bool MfaRequired { get; init; }

    /// <summary>Consecutive failures before lockout (FR-AUTH-011).</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumAllowedFailedAttempts,
        CompanyAuthenticationPolicy.MaximumAllowedFailedAttempts)]
    public int MaximumFailedAttempts { get; init; }

    /// <summary>How long a lockout lasts, in minutes.</summary>
    [Range(
        CompanyAuthenticationPolicy.MinimumLockoutMinutes,
        CompanyAuthenticationPolicy.MaximumLockoutMinutes)]
    public int LockoutMinutes { get; init; }
}

/// <summary>The policy in force.</summary>
/// <remarks>
/// <c>isCompanyConfigured</c> distinguishes "we chose these" from "nobody has chosen". The values
/// are identical either way; which it is decides whether a deployment default change would move
/// them.
/// </remarks>
public sealed record AuthenticationPolicyResponse(
    int MinimumPasswordLength,
    bool RequireBreachCheck,
    int IdleTimeoutMinutes,
    int AbsoluteLifetimeMinutes,
    bool MfaRequired,
    int MaximumFailedAttempts,
    int LockoutMinutes,
    bool IsCompanyConfigured);
