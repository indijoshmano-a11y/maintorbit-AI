using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Application.Modules.Identity.Commands.AuthenticationPolicy;
using MaintOrbit.Application.Modules.Identity.Commands.CompletePasswordReset;
using MaintOrbit.Application.Modules.Identity.Commands.RequestPasswordReset;
using MaintOrbit.Application.Modules.Identity.Commands.EmailVerification;
using MaintOrbit.Application.Modules.Identity.Commands.EmployeeRoles;
using MaintOrbit.Application.Modules.Identity.Commands.Login;
using MaintOrbit.Application.Modules.Identity.Commands.Mfa;
using MaintOrbit.Application.Modules.Identity.Commands.RotateRefreshToken;
using MaintOrbit.Application.Modules.Identity.Commands.SignIn;
using MaintOrbit.Application.Modules.Identity.Commands.Sessions;
using MaintOrbit.Application.Modules.Identity.Commands.SignOut;
using MaintOrbit.Application.Modules.Identity.Queries.EmployeeRoles;
using MaintOrbit.Application.Modules.Identity.Queries.Employees;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaintOrbit.Application.DependencyInjection;

/// <summary>
/// Registration seam for the application layer.
/// </summary>
/// <remarks>
/// Every layer owns its own registration and exposes exactly one entry point. The
/// composition root calls these in dependency order and knows nothing about what each
/// layer contains — which is what keeps registration from leaking upward into
/// <c>Program.cs</c> as the system grows.
/// <para>
/// This layer registers nothing yet. Command and query handlers, validators, the
/// behaviour pipeline, and mapping configuration are registered here as the milestones
/// that introduce them land. The seam exists now so that later milestones extend a
/// stable contract rather than reshaping the composition root.
/// </para>
/// </remarks>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers application-layer services.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered explicitly, one per use case. When the ADR-0012 dispatcher lands it resolves
        // handlers by this registration; nothing about the handler changes.
        services.TryAddScoped<
            ICommandHandler<AcceptInvitationCommand>,
            AcceptInvitationCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<LoginCommand, AuthenticationResult>,
            LoginCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<RotateRefreshTokenCommand, RefreshedTokens>,
            RotateRefreshTokenCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<RefreshSessionCommand, RefreshedTokens>,
            RefreshSessionCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<SignInCommand, SignInResult>,
            SignInCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<RequestPasswordResetCommand>,
            RequestPasswordResetCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<CompletePasswordResetCommand>,
            CompletePasswordResetCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<RequestEmailVerificationCommand>,
            RequestEmailVerificationCommandHandler>();

        services.TryAddScoped<ICommandHandler<VerifyEmailCommand>, VerifyEmailCommandHandler>();

        // Not a handler: the one place a presented second factor is judged, shared by
        // verification and by disabling so the two cannot answer differently.
        services.TryAddScoped<MfaChallengeVerifier>();

        services.TryAddScoped<
            ICommandHandler<BeginMfaEnrollmentCommand, MfaEnrollmentSecret>,
            BeginMfaEnrollmentCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<ConfirmMfaEnrollmentCommand, MfaRecoveryCodes>,
            ConfirmMfaEnrollmentCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<VerifyMfaChallengeCommand, MfaVerification>,
            VerifyMfaChallengeCommandHandler>();

        services.TryAddScoped<ICommandHandler<DisableMfaCommand>, DisableMfaCommandHandler>();

        services.TryAddScoped<ICommandHandler<RevokeSessionCommand>, RevokeSessionCommandHandler>();
        services.TryAddScoped<
            ICommandHandler<RevokeOtherSessionsCommand, int>,
            RevokeOtherSessionsCommandHandler>();
        services.TryAddScoped<
            ICommandHandler<RecordSessionActivityCommand>,
            RecordSessionActivityCommandHandler>();

        services.TryAddScoped<ICommandHandler<SignOutCommand>, SignOutCommandHandler>();
        services.TryAddScoped<
            ICommandHandler<SignOutEverywhereCommand>,
            SignOutEverywhereCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<AssignRoleCommand, AssignedRole>,
            AssignRoleCommandHandler>();

        services.TryAddScoped<ICommandHandler<RemoveRoleCommand>, RemoveRoleCommandHandler>();

        services.TryAddScoped<
            ICommandHandler<UpdateAuthenticationPolicyCommand, AuthenticationPolicyView>,
            UpdateAuthenticationPolicyCommandHandler>();

        // Queries. Registered against IQueryHandler rather than ICommandHandler because ADR-0012
        // treats them differently — no write transaction, no outbox — and the dispatcher tells
        // them apart by the contract they implement.
        services.TryAddScoped<
            IQueryHandler<ListEmployeesQuery, EmployeePage>,
            ListEmployeesQueryHandler>();

        services.TryAddScoped<
            IQueryHandler<GetCurrentEmployeeQuery, EmployeeSummary>,
            GetCurrentEmployeeQueryHandler>();

        services.TryAddScoped<
            IQueryHandler<ListEmployeeRolesQuery, EmployeeRoleList>,
            ListEmployeeRolesQueryHandler>();

        services.TryAddScoped<
            IQueryHandler<GetAuthenticationPolicyQuery, AuthenticationPolicyView>,
            GetAuthenticationPolicyQueryHandler>();

        services.TryAddScoped<
            IQueryHandler<ListSessionsQuery, IReadOnlyList<EmployeeSession>>,
            ListSessionsQueryHandler>();

        services.TryAddScoped<
            IQueryHandler<GetCurrentSessionQuery, EmployeeSession>,
            GetCurrentSessionQueryHandler>();

        return services;
    }
}
