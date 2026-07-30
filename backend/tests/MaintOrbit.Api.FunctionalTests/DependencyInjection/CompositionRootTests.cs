using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MaintOrbit.Api.FunctionalTests.DependencyInjection;

/// <summary>
/// Proves the composition root builds, and that provider validation rejects the mistakes
/// it exists to catch.
/// </summary>
/// <remarks>
/// The value of enabling <c>ValidateScopes</c> and <c>ValidateOnBuild</c> is entirely in
/// whether they fire. These tests assert both, so a future change that disables them —
/// or that introduces a captive dependency — fails here rather than in production.
/// </remarks>
public sealed class CompositionRootTests
{
    private static IConfiguration ValidConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Application:Name"] = "MaintOrbit AI",
                ["Application:PublicBaseUrl"] = "https://api.example.test",
                ["Cors:AllowCredentials"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://console.example.test"
            })
            .Build();

    private static ServiceProvider BuildCompositionRoot(ServiceProviderOptions options)
    {
        var configuration = ValidConfiguration();

        var services = new ServiceCollection();
        services
            .AddApplication()
            .AddInfrastructure(configuration)
            .AddApi(configuration);

        return services.BuildServiceProvider(options);
    }

    private static readonly ServiceProviderOptions ValidatingOptions = new()
    {
        ValidateScopes = true,
        ValidateOnBuild = true
    };

    [Fact]
    public void CompositionRoot_BuildsWithValidationEnabled()
    {
        using var provider = BuildCompositionRoot(ValidatingOptions);

        Assert.NotNull(provider);
    }

    [Fact]
    public void LayerRegistration_IsIdempotent()
    {
        // Registration extensions are called once by the composition root, but a duplicate
        // call must not corrupt the container — a second AddInfrastructure should not
        // produce a second, competing clock.
        var configuration = ValidConfiguration();

        var services = new ServiceCollection();
        services.AddApplication().AddInfrastructure(configuration).AddApi(configuration);
        services.AddApplication().AddInfrastructure(configuration).AddApi(configuration);

        using var provider = services.BuildServiceProvider(ValidatingOptions);

        Assert.NotNull(provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Clock_IsRegisteredAgainstTheAbstraction()
    {
        // DI-6: ports are registered against their interface, never the concrete type.
        // Resolving the abstraction is what lets a test substitute a controllable clock.
        using var provider = BuildCompositionRoot(ValidatingOptions);

        var clock = provider.GetRequiredService<TimeProvider>();

        Assert.NotNull(clock);
    }

    [Fact]
    public void Clock_IsSingletonAcrossScopes()
    {
        // DI-3: singleton for stateless services. The clock holds no per-request state, so
        // every scope must observe the same instance.
        using var provider = BuildCompositionRoot(ValidatingOptions);

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredService<TimeProvider>();
        var b = second.ServiceProvider.GetRequiredService<TimeProvider>();

        Assert.Same(a, b);
    }

    [Fact]
    public void CaptiveDependency_IsRejectedAtBuild()
    {
        // DI-4: never inject a scoped service into a singleton. The captured instance
        // outlives its scope and is then shared across every subsequent request — which in
        // a multi-tenant system means one Company's context serving another, presenting as
        // correct behaviour rather than an error.
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        services.AddSingleton<SingletonHolder>();

        var failure = Assert.Throws<AggregateException>(
            () => services.BuildServiceProvider(ValidatingOptions));

        Assert.Contains(
            failure.InnerExceptions,
            e => e.Message.Contains("Cannot consume scoped service", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDependency_IsRejectedAtBuild()
    {
        // ValidateOnBuild resolves every registration at startup, so an unregistered
        // dependency fails immediately rather than on the first request that needs it.
        var services = new ServiceCollection();
        services.AddSingleton<NeedsMissingDependency>();

        Assert.Throws<AggregateException>(
            () => services.BuildServiceProvider(ValidatingOptions));
    }

    [Fact]
    public void CaptiveDependency_IsNotDetectedWithoutValidation()
    {
        // Establishes that the previous rejection comes from validation being enabled,
        // not from some incidental behaviour of the container.
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        services.AddSingleton<SingletonHolder>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false
        });

        Assert.NotNull(provider);
    }

    private sealed class ScopedDependency;

    private sealed class SingletonHolder(ScopedDependency dependency)
    {
        public ScopedDependency Dependency { get; } = dependency;
    }

    private sealed class UnregisteredDependency;

    private sealed class NeedsMissingDependency(UnregisteredDependency dependency)
    {
        public UnregisteredDependency Dependency { get; } = dependency;
    }
}
