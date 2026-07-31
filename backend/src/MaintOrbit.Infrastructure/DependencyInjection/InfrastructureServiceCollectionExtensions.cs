using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Infrastructure.Authentication;
using MaintOrbit.Infrastructure.MultiTenancy;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Infrastructure.Persistence.Interceptors;
using MaintOrbit.Infrastructure.Telemetry;
using MaintOrbit.Shared.Abstractions;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Infrastructure.DependencyInjection;

/// <summary>
/// Registration seam for the infrastructure layer.
/// </summary>
/// <remarks>
/// Infrastructure implements the ports declared in the application layer, so it is the
/// layer that supplies concrete adapters — persistence, caching, provider clients,
/// telemetry, and the system clock. Each is registered against its abstraction, never its
/// concrete type (DI-6), so that a caller cannot take a dependency on an implementation
/// detail even by accident.
/// <para>
/// The clock and the correlation accessor are registered at this milestone. Persistence,
/// caching, messaging, and provider adapters arrive with the milestones that introduce them.
/// </para>
/// </remarks>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers infrastructure-layer services.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddClock(services);
        AddCorrelation(services);
        AddTenantContext(services);
        AddPersistence(services, configuration);
        AddPasswordHashing(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the ambient tenant context.
    /// </summary>
    /// <remarks>
    /// Singleton for the same reason as the correlation accessor: the accessor is stateless and
    /// the value lives in the caller's execution context (DI-3). A scoped registration would make
    /// every component that reads the tenant request-scoped by contagion, and would leave the
    /// Worker unable to establish context at all — which TC-5 requires it to do.
    /// </remarks>
    private static void AddTenantContext(IServiceCollection services)
    {
        services.TryAddSingleton<ITenantContext, TenantContextAccessor>();
    }

    /// <summary>
    /// Registers the database context and its settings.
    /// </summary>
    /// <remarks>
    /// <see cref="MaintOrbitDbContext"/> is scoped — the EF default and the right one. A context
    /// carries change-tracking state for a unit of work, so sharing one across requests would
    /// leak entities between them, and under multi-tenancy that means leaking them between
    /// Companies.
    /// <para>
    /// <b>Not <c>AddDbContextPool</c>.</b> Context pooling resets and reuses context instances,
    /// which interacts with the connection pooling mode that DD-2 has not settled —
    /// <c>docs/06-database/database-design.md</c> §5 records that mode as blocking
    /// implementation, and §6.7 explains that it is a security decision. Adding a second layer
    /// of reuse before the first is decided would prejudge it.
    /// </para>
    /// </remarks>
    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddSingleton, not TryAddSingleton. ValidateDataAnnotations already registers an
        // IValidateOptions<PersistenceOptions>, so TryAdd sees the service type as present and
        // silently does nothing — leaving the cross-property rules unenforced while every
        // isolated test of the validator still passes.
        services.AddSingleton<IValidateOptions<PersistenceOptions>, PersistenceOptionsValidator>();

        services.AddDbContext<MaintOrbitDbContext>((provider, builder) =>
        {
            NpgsqlConfiguration.Apply(
                builder,
                provider.GetRequiredService<IOptions<PersistenceOptions>>().Value);

            // Applies the tenant session variable at checkout and clears it at return (TC-4).
            // Registered here rather than in NpgsqlConfiguration so the design-time factory,
            // which has no service provider and no tenant, does not need one.
            builder.AddInterceptors(
                new TenantConnectionInterceptor(provider.GetRequiredService<ITenantContext>()));
        });
    }

    /// <summary>
    /// Registers password hashing and its parameters.
    /// </summary>
    /// <remarks>
    /// Singleton: the hasher holds no per-request state, and reads its parameters through
    /// <c>IOptions</c> on each call rather than capturing them (DI-3). Registered against
    /// <see cref="IPasswordHasher"/> so no caller can reach the algorithm (DI-6) — which is what
    /// makes replacing it a configuration and re-hash exercise rather than a code change.
    /// </remarks>
    private static void AddPasswordHashing(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PasswordHashingOptions>()
            .Bind(configuration.GetSection(PasswordHashingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddSingleton, not TryAddSingleton: ValidateDataAnnotations has already registered an
        // IValidateOptions<PasswordHashingOptions>, and TryAdd would see the service type as
        // present and silently do nothing.
        services.AddSingleton<IValidateOptions<PasswordHashingOptions>, PasswordHashingOptionsValidator>();

        services.TryAddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
    }

    /// <summary>
    /// Registers the ambient correlation identifier accessor.
    /// </summary>
    /// <remarks>
    /// Registered as a singleton because the accessor is stateless — the identifier it reads
    /// lives in the caller's execution context, not in the object (DI-3). That also makes it
    /// safe to inject into other singletons, which matters: a scoped registration here would
    /// make every logging component request-scoped by contagion, and would leave the Worker
    /// with no way to correlate at all.
    /// <para>
    /// Registered against <see cref="ICorrelationIdAccessor"/> (DI-6). The implementation is
    /// internal, so nothing outside this assembly can take a dependency on the
    /// <see cref="AsyncLocal{T}"/> mechanism even deliberately.
    /// </para>
    /// </remarks>
    private static void AddCorrelation(IServiceCollection services)
    {
        services.TryAddSingleton<ICorrelationIdAccessor, CorrelationIdAccessor>();
    }

    /// <summary>
    /// Registers the system clock.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider"/> is the time abstraction the codebase depends on.
    /// Coding standard U-3 forbids <c>DateTime.Now</c> and <c>DateTime.UtcNow</c> outside
    /// it, and architecture test AT-9 enforces that — which only works if there is
    /// something to inject instead.
    /// <para>
    /// Registered as a singleton because the clock holds no per-request state (DI-3), and
    /// against the abstract <see cref="TimeProvider"/> rather than a concrete type (DI-6)
    /// so that tests substitute a controllable clock without the code under test knowing.
    /// </para>
    /// </remarks>
    private static void AddClock(IServiceCollection services)
    {
        // TryAdd, matching the correlation accessor. A plain Add makes a repeated
        // AddInfrastructure call register the clock twice: GetRequiredService still returns the
        // last one, so nothing appears wrong, while GetServices yields two. That is harmless
        // only because TimeProvider.System is a shared static — the day a controllable clock is
        // registered the same way, half the system would resolve a different instance.
        services.TryAddSingleton(TimeProvider.System);
    }
}
