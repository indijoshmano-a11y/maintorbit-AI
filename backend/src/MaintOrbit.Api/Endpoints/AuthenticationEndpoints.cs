using System.ComponentModel.DataAnnotations;
using MaintOrbit.Api.Configuration;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Modules.Identity.Commands.CompletePasswordReset;
using MaintOrbit.Application.Modules.Identity.Commands.EmailVerification;
using MaintOrbit.Application.Modules.Identity.Commands.RequestPasswordReset;
using MaintOrbit.Application.Modules.Identity.Commands.Mfa;
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

        // Email verification (FR-AUTH-013). §3.1 lists it among this group's operations.
        //
        // The two halves have opposite authentication requirements, and both are forced. Issuing a
        // link is for the caller's own address, so it needs a session — without one, a caller could
        // have verification mail sent to anybody. Redeeming one is opened from an email in whatever
        // browser is to hand, so it must not need a session: verification gates activation, and
        // requiring a session would make it reachable only by people who are already active.
        var email = group.MapGroup("/email");

        email.MapPost("/verify/request", RequestEmailVerificationAsync).RequireAuthorization();
        email.MapPost("/verify", VerifyEmailAsync);

        // §3.1: "MFA management requires an authenticated session". All four, including verify —
        // this is step-up within a live session, not the sign-in challenge, which needs the
        // Company MFA policy FR-AUTH-006 describes and the tenancy module does not yet hold.
        var mfa = group.MapGroup("/mfa").RequireAuthorization();

        mfa.MapPost("/enroll", BeginMfaEnrollmentAsync);
        mfa.MapPost("/confirm", ConfirmMfaEnrollmentAsync);
        mfa.MapPost("/verify", VerifyMfaChallengeAsync);
        mfa.MapPost("/disable", DisableMfaAsync);

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

    /// <summary>Issues a TOTP secret for the authenticated Employee (FR-AUTH-005).</summary>
    /// <remarks>
    /// The Employee is taken from the validated token, never from the request. A body naming one
    /// would let a caller enrol a factor on somebody else's account — takeover rather than
    /// protection.
    /// </remarks>
    private static async Task<IResult> BeginMfaEnrollmentAsync(
        HttpContext context,
        ICommandHandler<BeginMfaEnrollmentCommand, MfaEnrollmentSecret> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new BeginMfaEnrollmentCommand(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new MfaEnrollmentResponse(result.Value.Secret, result.Value.Uri))
            : MfaProblem(context, result.Error);
    }

    /// <summary>Proves possession and turns the factor on, returning the recovery codes once.</summary>
    private static async Task<IResult> ConfirmMfaEnrollmentAsync(
        MfaCodeRequest request,
        HttpContext context,
        ICommandHandler<ConfirmMfaEnrollmentCommand, MfaRecoveryCodes> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler
            .HandleAsync(new ConfirmMfaEnrollmentCommand(request.Code), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new MfaRecoveryCodesResponse(result.Value.Codes))
            : MfaProblem(context, result.Error);
    }

    /// <summary>Satisfies a second-factor challenge with a TOTP code or a recovery code.</summary>
    private static async Task<IResult> VerifyMfaChallengeAsync(
        MfaCodeRequest request,
        HttpContext context,
        ICommandHandler<VerifyMfaChallengeCommand, MfaVerification> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler
            .HandleAsync(new VerifyMfaChallengeCommand(request.Code), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new MfaVerificationResponse(
                result.Value.UsedRecoveryCode, result.Value.RemainingRecoveryCodes))
            : MfaProblem(context, result.Error);
    }

    /// <summary>Turns the second factor off, against a current code.</summary>
    private static async Task<IResult> DisableMfaAsync(
        MfaCodeRequest request,
        HttpContext context,
        ICommandHandler<DisableMfaCommand> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler
            .HandleAsync(new DisableMfaCommand(request.Code), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Results.NoContent() : MfaProblem(context, result.Error);
    }

    /// <summary>
    /// Maps an MFA failure to its documented status.
    /// </summary>
    /// <remarks>
    /// §7's table: <c>validation_failed</c> is 400, <c>conflict</c> is 409, and
    /// <c>authentication_failed</c> is 401. A wrong code, a replayed one, and a reused recovery
    /// code are all the last of those with one description — telling them apart would say which
    /// guess was close, and "that code was right but already spent" is the most useful thing an
    /// attacker could learn.
    /// </remarks>
    private static IResult MfaProblem(HttpContext context, Error error) =>
        Problem(context, error, error.Code switch
        {
            "validation_failed" => StatusCodes.Status400BadRequest,
            "conflict" => StatusCodes.Status409Conflict,
            "not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status401Unauthorized
        });

    /// <summary>
    /// Issues a verification link for the caller's own address (FR-AUTH-013).
    /// </summary>
    /// <remarks>
    /// <c>202 Accepted</c>, per §7's table for an asynchronous operation: the work that matters
    /// happens in a message the caller cannot observe. The Employee comes from the validated token
    /// and there is no body, so there is nothing here to point at somebody else's address.
    /// </remarks>
    private static async Task<IResult> RequestEmailVerificationAsync(
        HttpContext context,
        ICommandHandler<RequestEmailVerificationCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .HandleAsync(new RequestEmailVerificationCommand(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Accepted()
            : Problem(context, result.Error, StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Redeems a verification link and records the address as proved (FR-AUTH-013).
    /// </summary>
    /// <remarks>
    /// A missing field is <c>400 validation_failed</c>. Every other failure — unknown, expired,
    /// already used, superseded, or issued for an address that has since changed — is one
    /// <c>401</c> with one description, because telling them apart tells whoever is probing which
    /// of their guesses was a real token.
    /// </remarks>
    private static async Task<IResult> VerifyEmailAsync(
        EmailVerificationRequest request,
        HttpContext context,
        ICommandHandler<VerifyEmailCommand> handler,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var result = await handler
            .HandleAsync(new VerifyEmailCommand(request.Token), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Results.NoContent();
        }

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
            Title = error.Code switch
            {
                "validation_failed" => "The request is not valid",
                "conflict" => "The request conflicts with the current state",
                "not_found" => "Not found",
                _ => "Authentication failed"
            },
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
