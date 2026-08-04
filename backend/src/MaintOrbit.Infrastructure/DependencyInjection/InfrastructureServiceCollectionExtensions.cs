using MaintOrbit.Application.Abstractions.Authorization;
using MaintOrbit.Application.Abstractions.Persistence;
using MaintOrbit.Infrastructure.Authorization;
using MaintOrbit.Application.Abstractions.Security;
using MaintOrbit.Application.Common.Configuration;
using MaintOrbit.Domain.Modules.Identity.Repositories;
using MaintOrbit.Infrastructure.Persistence.Repositories.Identity;
using MaintOrbit.Infrastructure.Authentication;
using MaintOrbit.Application.Abstractions.Notifications;
using MaintOrbit.Infrastructure.Caching;
using MaintOrbit.Infrastructure.Cryptography;
using MaintOrbit.Infrastructure.MultiTenancy;
using MaintOrbit.Infrastructure.Notifications;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Infrastructure.Persistence.Interceptors;
using MaintOrbit.Infrastructure.Telemetry;
using MaintOrbit.Shared.Abstractions;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

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
        AddRepositories(services);
        AddAccessTokens(services, configuration);
        AddSessions(services, configuration);
        AddAuthenticationPolicy(services, configuration);
        AddPermissionCache(services, configuration);
        AddAuthorization(services);
        AddPasswordReset(services, configuration);
        AddEncryption(services, configuration);
        AddMfa(services, configuration);

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
    /// Registers session and refresh token settings and the token factory.
    /// </summary>
    /// <remarks>
    /// The factory is a singleton: it holds no per-request state, reads its settings through
    /// <c>IOptions</c>, and its only dependency is the system random number generator.
    /// </remarks>
    private static void AddSessions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddSingleton, not TryAddSingleton: ValidateDataAnnotations has already registered an
        // IValidateOptions<SessionOptions>, so TryAdd would silently do nothing.
        services.AddSingleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>();

        services.AddOptions<RefreshTokenOptions>()
            .Bind(configuration.GetSection(RefreshTokenOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IRefreshTokenFactory, RefreshTokenFactory>();

        // Scoped: it reads a session through the repository, which shares the request's DbContext
        // and therefore the tenant scope the caller opened.
        services.TryAddScoped<ISessionValidator, SessionValidator>();

        // The only path that reads across Companies (04-tenant-security §3.4). Singleton because
        // it holds no state and opens its own connection per call.
        services.TryAddSingleton<ICredentialDirectory, ElevatedCredentialDirectory>();
    }

    /// <summary>
    /// Registers password reset token issuance and the notification seam (FR-AUTH-012).
    /// </summary>
    /// <remarks>
    /// The factory is a singleton: it holds no per-request state, reads its settings through
    /// <c>IOptions</c>, and its only dependency is the system random number generator.
    /// <para>
    /// The notifier registered here <b>does not send mail</b> — see
    /// <see cref="UndeliveredPasswordResetNotifier"/>. It is registered rather than omitted so the
    /// handler composes and the gap is visible in the log, instead of the container failing at
    /// startup for a port nothing can yet satisfy.
    /// </para>
    /// </remarks>
    private static void AddPasswordReset(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PasswordResetOptions>()
            .Bind(configuration.GetSection(PasswordResetOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IPasswordResetTokenFactory, PasswordResetTokenFactory>();
        services.TryAddSingleton<IPasswordResetNotifier, UndeliveredPasswordResetNotifier>();
    }

    /// <summary>
    /// Registers per-Company authentication policy (§3.10).
    /// </summary>
    /// <remarks>
    /// The provider is scoped: it reads through the repository and therefore the request's tenant
    /// scope. The defaults are options, validated on start — a deployment whose default policy no
    /// Company could save refuses to run rather than failing on somebody's first sign-in.
    /// </remarks>
    private static void AddAuthenticationPolicy(
        IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthenticationPolicyDefaults>()
            .Bind(configuration.GetSection(AuthenticationPolicyDefaults.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddSingleton, not TryAddSingleton: ValidateDataAnnotations has already registered an
        // IValidateOptions<AuthenticationPolicyDefaults>, and TryAdd would see the service type as
        // present and silently do nothing — leaving the relational rule unenforced.
        services.AddSingleton<IValidateOptions<AuthenticationPolicyDefaults>,
            AuthenticationPolicyDefaultsValidator>();

        services.TryAddScoped<IAuthenticationPolicyProvider, AuthenticationPolicyProvider>();
    }

    /// <summary>
    /// Registers the permission cache (ADR-0006).
    /// </summary>
    /// <remarks>
    /// <b>Which implementation is a configuration decision, made once, at startup.</b> With a
    /// connection string, Redis; without one, a cache that stores nothing and resolves every
    /// request from the database. Deciding per call would make the failure semantics an accident
    /// of authorship, which is exactly what ADR-0021 forbids.
    /// <para>
    /// The multiplexer is a singleton and is created lazily: StackExchange.Redis multiplexes over
    /// one connection, and building a second per request is the misconfiguration
    /// backend-technologies §5.2 warns produces latency outliers. <c>AbortOnConnectFail</c> is
    /// cleared so an unreachable server is an outage the cache survives rather than a host that
    /// will not start — Redis is not permitted to become a hard dependency of authorization.
    /// </para>
    /// </remarks>
    private static void AddPermissionCache(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PermissionCacheOptions>()
            .Bind(configuration.GetSection(PermissionCacheOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddSingleton, not TryAddSingleton: ValidateDataAnnotations has already registered an
        // IValidateOptions<PermissionCacheOptions>, and TryAdd would see the service type as
        // present and silently do nothing — leaving the sixty-second bound unenforced.
        services.AddSingleton<IValidateOptions<PermissionCacheOptions>,
            PermissionCacheOptionsValidator>();

        // Registered as its own service rather than captured in the cache's closure, so the
        // container owns its lifetime and disposes it with the host. A multiplexer held only by a
        // lambda is a TCP connection that outlives everything that used it — invisible in one
        // process, and a connection leak per host in a test suite that builds many.
        //
        // Never resolved when the cache is disabled, so a deployment without Redis makes no
        // connection attempt at all.
        services.TryAddSingleton<IConnectionMultiplexer>(provider =>
        {
            var value = provider.GetRequiredService<IOptions<PermissionCacheOptions>>().Value;

            var settings = ConfigurationOptions.Parse(value.ConnectionString);

            // An unreachable server must degrade, not stop the host. Redis is not permitted to
            // become a hard dependency of authorization.
            settings.AbortOnConnectFail = false;

            return ConnectionMultiplexer.Connect(settings);
        });

        services.TryAddSingleton<IPermissionCache>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PermissionCacheOptions>>();

            return options.Value.IsEnabled
                ? new RedisPermissionCache(
                    provider.GetRequiredService<IConnectionMultiplexer>(),
                    options,
                    provider.GetRequiredService<ILogger<RedisPermissionCache>>())
                : new DisabledPermissionCache();
        });
    }

    /// <summary>
    /// Registers permission resolution.
    /// </summary>
    /// <remarks>
    /// Scoped, because resolution reads through the repository and therefore the request's tenant
    /// scope. The cache it reads through is a singleton — outliving a request is the whole point.
    /// </remarks>
    private static void AddAuthorization(IServiceCollection services)
    {
        services.TryAddScoped<IPermissionService, PermissionService>();
        services.TryAddScoped<IAuthorizationEvaluator, AuthorizationEvaluator>();
    }

    /// <summary>
    /// Registers access token issuance and validation.
    /// </summary>
    /// <remarks>
    /// All singletons. The key ring imports its RSA keys once — creating an <c>RSA</c> instance is
    /// expensive and the same key serves every request, so a per-request import would put a
    /// keypair parse on the authentication path. The generator and validator hold no per-request
    /// state and read their settings through <c>IOptions</c>.
    /// <para>
    /// Registered against the ports (DI-6), so nothing outside this assembly can reach the JWT
    /// library — which is what lets the token format change without a caller noticing.
    /// </para>
    /// </remarks>
    private static void AddAccessTokens(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddSingleton, not TryAddSingleton: ValidateDataAnnotations has already registered an
        // IValidateOptions<JwtOptions>, and TryAdd would see the service type as present and
        // silently do nothing — leaving the key checks unenforced.
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services.TryAddSingleton<SigningKeyRing>();
        services.TryAddSingleton<IAccessTokenValidationParametersFactory,
            AccessTokenValidationParametersFactory>();
        services.TryAddSingleton<IAccessTokenGenerator, JwtAccessTokenGenerator>();
        services.TryAddSingleton<IAccessTokenValidator, JwtAccessTokenValidator>();
    }

    /// <summary>
    /// Registers the identity repositories and the unit of work.
    /// </summary>
    /// <remarks>
    /// Scoped, matching <see cref="MaintOrbitDbContext"/>. All three resolve the same context
    /// instance within a scope, and that shared instance <i>is</i> the unit of work — a repository
    /// holding a different context would track its aggregate somewhere the commit never looks.
    /// <para>
    /// Registered here rather than by assembly scanning. Twelve modules will each add a few
    /// repositories, and a scan would register whatever happened to implement the shape, including
    /// a test double left in the wrong assembly.
    /// </para>
    /// </remarks>
    private static void AddRepositories(IServiceCollection services)
    {
        // Singleton: the decoy hash is derived once per process. Per-request derivation would
        // make the enumeration defence a denial-of-service amplifier of its own (T-5).
        services.TryAddSingleton<IDecoyPasswordHash, DecoyPasswordHash>();

        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped<IEmployeeRepository, EmployeeRepository>();
        services.TryAddScoped<IEmployeeCredentialRepository, EmployeeCredentialRepository>();
        services.TryAddScoped<ISessionRepository, SessionRepository>();
        services.TryAddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.TryAddScoped<IAuthorizationRepository, AuthorizationRepository>();
        services.TryAddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();
        services.TryAddScoped<
            ICompanyAuthenticationPolicyRepository,
            CompanyAuthenticationPolicyRepository>();
        services.TryAddScoped<IMfaEnrollmentRepository, MfaEnrollmentRepository>();
        services.TryAddScoped<IMfaRecoveryCodeRepository, MfaRecoveryCodeRepository>();
    }

    /// <summary>
    /// Registers application-layer encryption (SD-009).
    /// </summary>
    /// <remarks>
    /// Validated on start, so a deployment that cannot decrypt its C4 data refuses to run rather
    /// than discovering it on an Employee's second-factor prompt. Both registrations are
    /// singletons: the key is decoded once, and <see cref="AesGcmEnvelopeEncryptor"/> holds no
    /// per-request state.
    /// </remarks>
    private static void AddEncryption(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EncryptionOptions>()
            .Bind(configuration.GetSection(EncryptionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddSingleton, not TryAddSingleton: ValidateDataAnnotations has already registered an
        // IValidateOptions<EncryptionOptions>, and TryAdd would see the service type as present
        // and silently do nothing — leaving the key checks unenforced.
        services.AddSingleton<IValidateOptions<EncryptionOptions>, EncryptionOptionsValidator>();

        services.TryAddSingleton<ICompanyDataKeyStore, DeploymentDataKeyStore>();
        services.TryAddSingleton<IEnvelopeEncryptor, AesGcmEnvelopeEncryptor>();
    }

    /// <summary>
    /// Registers TOTP multi-factor authentication (FR-AUTH-005).
    /// </summary>
    /// <remarks>
    /// Both services are singletons — they hold no per-request state and their only dependencies
    /// are settings and the system random number generator. Neither reaches the database; the
    /// repositories do that, and they are scoped like every other one.
    /// </remarks>
    private static void AddMfa(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MfaOptions>()
            .Bind(configuration.GetSection(MfaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<ITotpService, Rfc6238TotpService>();
        services.TryAddSingleton<IRecoveryCodeFactory, RecoveryCodeFactory>();
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
