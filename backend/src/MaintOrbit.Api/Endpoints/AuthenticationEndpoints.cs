using System.ComponentModel.DataAnnotations;
using MaintOrbit.Api.Configuration;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Modules.Identity.Commands.RotateRefreshToken;
using MaintOrbit.Application.Modules.Identity.Commands.SignIn;
using MaintOrbit.Application.Modules.Identity.Commands.SignOut;
using MaintOrbit.Domain.Common.Results;
using MaintOrbit.Domain.Modules.Identity.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

// Disambiguated from Microsoft.AspNetCore.Mvc.SignInResult, which is an action result.
using SignInOutcome = MaintOrbit.Application.Modules.Identity.Commands.SignIn.SignInResult;

namespace MaintOrbit.Api.Endpoints;

/// <summary>
/// The authentication endpoints — <c>/api/v1/auth</c>.
/// </summary>
/// <remarks>
/// Mounted under the configured base path, which api-specification §1.4 and ADR-0016 fix at
/// <c>/api/v1</c>. §3.1 names this group and describes it as "mostly unauthenticated": sign-in and
/// refresh establish a session and therefore cannot require one; sign-out ends the session it is
/// authenticated with, and requires one.
/// <para>
/// Every endpoint is a thin translation. It binds a request, calls one handler, and maps the
/// result — no branching on state, no orchestration. The rules live in the handlers, where they
/// are testable without HTTP.
/// </para>
/// </remarks>
public static class AuthenticationEndpoints
{
    /// <summary>Maps the authentication endpoints.</summary>
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var basePath = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ApiOptions>>().Value.BasePath;

        var group = endpoints.MapGroup($"{basePath}/auth");

        group.MapPost("/login", SignInAsync);
        group.MapPost("/refresh", RefreshAsync);

        // Sign-out ends the session the caller presented, so it must have presented one.
        group.MapPost("/logout", SignOutAsync).RequireAuthorization();
        group.MapPost("/logout-all", SignOutEverywhereAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest request,
        HttpContext context,
        ICommandHandler<SignInCommand, SignInOutcome> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler.HandleAsync(
            new SignInCommand(
                request.Email,
                request.Password,
                ParseClientType(request.ClientType),
                request.DeviceLabel,
                // Server-observed, never taken from the body. A caller-supplied address would be a
                // caller writing their own entry in someone's device list.
                context.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new SignInResponse(
                result.Value.AccessToken.Value,
                result.Value.RefreshToken,
                result.Value.AccessToken.ExpiresAtUtc,
                result.Value.SessionId.ToString()))
            : Problem(context, result.Error, StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        HttpContext context,
        ICommandHandler<RefreshSessionCommand, RefreshedTokens> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler
            .HandleAsync(new RefreshSessionCommand(request.RefreshToken), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new RefreshResponse(
                result.Value.AccessToken.Value,
                result.Value.RefreshToken,
                result.Value.AccessToken.ExpiresAtUtc))
            : Problem(context, result.Error, StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> SignOutAsync(
        HttpContext context,
        ICommandHandler<SignOutCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SignOutCommand(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.NoContent()
            : Problem(context, result.Error, StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> SignOutEverywhereAsync(
        HttpContext context,
        ICommandHandler<SignOutEverywhereCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SignOutEverywhereCommand(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.NoContent()
            : Problem(context, result.Error, StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Maps an unrecognised client type to <see cref="SessionClientType.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// Parsed rather than rejected: the value is descriptive, shown in the Employee's device list,
    /// and refusing a sign-in because a client sent an unfamiliar label would make the label
    /// load-bearing. It is never an authorization input.
    /// </remarks>
    private static SessionClientType ParseClientType(string? value) =>
        Enum.TryParse<SessionClientType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SessionClientType.Unknown;

    /// <summary>
    /// Runs DataAnnotations and returns the documented validation envelope, or null when valid.
    /// </summary>
    /// <remarks>
    /// §4.5 gives field-level errors their own array with a dotted path and a machine-readable
    /// code, and §4.3 requires all failures returned together rather than one at a time.
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
                    code = "required",
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
    private static IResult Problem(HttpContext context, Error error, int statusCode)
    {
        var problem = new ProblemDetails
        {
            Type = error.Code,
            Title = "Authentication failed",
            Status = statusCode,
            Detail = error.Description
        };

        problem.Extensions["correlationId"] = context.RequestServices
            .GetService<Shared.Abstractions.ICorrelationIdAccessor>()?.Current;
        problem.Extensions["retryable"] = false;

        return Results.Json(
            problem,
            Authentication.AuthenticationServiceCollectionExtensions.ProblemJson,
            contentType: "application/problem+json",
            statusCode: statusCode);
    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
