using System.ComponentModel.DataAnnotations;
using MaintOrbit.Api.Configuration;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Modules.Identity.Commands.CompletePasswordReset;
using MaintOrbit.Application.Modules.Identity.Commands.RequestPasswordReset;
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

        // Unauthenticated by necessity: an Employee who has forgotten their password cannot
        // present one, and requiring a session would make the flow reachable only by people who
        // do not need it.
        group.MapPost("/password-reset/request", RequestPasswordResetAsync);
        group.MapPost("/password-reset/complete", CompletePasswordResetAsync);

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

    /// <summary>
    /// Asks for a reset link, and answers the same way regardless (FR-AUTH-012).
    /// </summary>
    /// <remarks>
    /// <b>Always <c>202 Accepted</c>.</b> §7 lists 202 for an asynchronous operation, which this
    /// is — the work that matters happens in a message the caller cannot observe. The status, the
    /// body, and the headers are identical whether the address belongs to an Employee or to
    /// nobody, because any difference is an account-enumeration oracle reachable without a
    /// credential.
    /// <para>
    /// A malformed address still returns 202 rather than 400. Validating the shape here would
    /// answer "is this even an address?", which is a smaller leak than the account check but the
    /// same kind — and one an attacker can use to clean a list before probing it.
    /// </para>
    /// </remarks>
    private static async Task<IResult> RequestPasswordResetAsync(
        PasswordResetRequest request,
        HttpContext context,
        ICommandHandler<RequestPasswordResetCommand> handler,
        CancellationToken cancellationToken)
    {
        await handler.HandleAsync(
            new RequestPasswordResetCommand(
                request.Email,
                // Server-observed, never taken from the body. A caller-supplied address would be
                // a caller writing their own entry in somebody's recovery history.
                context.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);

        // The handler cannot fail, and the endpoint does not inspect the result — reading it
        // would create somewhere for a future difference to leak out of.
        return Results.Accepted();
    }

    /// <summary>Redeems a reset link and sets the new password.</summary>
    /// <remarks>
    /// A missing field is <c>400 validation_failed</c> and says nothing about any account. Every
    /// other failure — unknown, expired, already used, superseded — is one <c>401</c> with one
    /// description, because telling them apart tells whoever is probing which of their guesses
    /// was a real token.
    /// </remarks>
    private static async Task<IResult> CompletePasswordResetAsync(
        PasswordResetCompletion request,
        HttpContext context,
        ICommandHandler<CompletePasswordResetCommand> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler.HandleAsync(
            new CompletePasswordResetCommand(request.Token, request.NewPassword),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Results.NoContent();
        }

        // §7 maps validation_failed to 400 and authentication_failed to 401. The handler chooses
        // which; the endpoint only carries the documented status for the code it was given.
        var status = result.Error.Code == "validation_failed"
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status401Unauthorized;

        return Problem(context, result.Error, status);
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
    /// <remarks>
    /// The title comes from the error code rather than being fixed, because this now serves two:
    /// a reset completion missing a field is a validation failure, and calling it "Authentication
    /// failed" would misdescribe it to every client that renders the title.
    /// </remarks>
    private static IResult Problem(HttpContext context, Error error, int statusCode)
    {
        var problem = new ProblemDetails
        {
            Type = error.Code,
            Title = error.Code == "validation_failed"
                ? "The request is not valid"
                : "Authentication failed",
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
