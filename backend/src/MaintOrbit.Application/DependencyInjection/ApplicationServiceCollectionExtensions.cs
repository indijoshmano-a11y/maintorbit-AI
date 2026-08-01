using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Application.Modules.Identity.Commands.Login;
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

        return services;
    }
}
