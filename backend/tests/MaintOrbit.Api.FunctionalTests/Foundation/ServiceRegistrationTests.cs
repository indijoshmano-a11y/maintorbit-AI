using System.Reflection;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MaintOrbit.Api.FunctionalTests.Foundation;

/// <summary>
/// Inspects the composition root as data, rather than resolving services one at a time.
/// </summary>
/// <remarks>
/// Every other DI test names the service it expects. That catches a service that stopped working
/// and misses a service nobody thought to name — which is the one that breaks. These rules apply
/// to the whole registration set, so a service added in a later milestone is covered the moment
/// it is registered.
/// </remarks>
public sealed class ServiceRegistrationTests
{
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Application:Name"] = "MaintOrbit AI",
                ["Application:PublicBaseUrl"] = "https://api.example.test",
                ["Cors:AllowCredentials"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                ["Persistence:ConnectionString"] =
                    "Host=localhost;Database=maintorbit_test;Username=maintorbit"
            })
            .Build();

    private static ServiceCollection Compose()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();

        services
            .AddApplication()
            .AddInfrastructure(configuration)
            .AddApi(configuration)
            .AddObservability(configuration);

        return services;
    }

    /// <summary>Registrations whose service or implementation type this codebase owns.</summary>
    private static IEnumerable<ServiceDescriptor> OwnedBy(ServiceCollection services) =>
        services.Where(static descriptor =>
            IsOurs(descriptor.ServiceType) || IsOurs(descriptor.ImplementationType));

    private static bool IsOurs(Type? type) =>
        type?.Assembly.GetName().Name?.StartsWith("MaintOrbit", StringComparison.Ordinal) == true;

    [Fact]
    public void EveryRegistrationWeOwn_Resolves()
    {
        // ValidateOnBuild builds call sites; it does not construct. A registration whose
        // constructor throws, or whose factory reads configuration that is absent, still passes
        // it. Resolving for real is what closes that gap.
        var services = Compose();
        var ourServiceTypes = OwnedBy(services)
            .Select(static descriptor => descriptor.ServiceType)
            .Where(static type => !type.IsGenericTypeDefinition)
            .Distinct()
            .ToList();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();

        var failures = new List<string>();

        foreach (var type in ourServiceTypes)
        {
            try
            {
                Assert.NotNull(scope.ServiceProvider.GetRequiredService(type));
            }
            catch (Exception exception)
            {
                failures.Add($"{type.Name}: {exception.Message}");
            }
        }

        Assert.Empty(failures);
        Assert.NotEmpty(ourServiceTypes);
    }

    [Fact]
    public void NoServiceWeOwn_IsRegisteredTwiceWithTheSameImplementation()
    {
        // An exact duplicate is always a mistake: the container keeps both, resolves the last,
        // and enumerating the service type yields the same implementation twice. Distinct
        // implementations of one service type are deliberate — several IValidateOptions for one
        // options class, for instance — so only exact pairs are flagged.
        var duplicates = OwnedBy(Compose())
            .Where(static descriptor => descriptor.ImplementationType is not null)
            .GroupBy(static descriptor => (descriptor.ServiceType, descriptor.ImplementationType))
            .Where(static group => group.Count() > 1)
            .Select(static group => $"{group.Key.ServiceType.Name} -> {group.Key.ImplementationType!.Name}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void LayerRegistration_RemainsIdempotent()
    {
        // The composition root calls each extension once, but nothing enforces that. Calling
        // twice must not corrupt the container — a second clock or a second correlation accessor
        // would be a different instance holding different state.
        var configuration = Configuration();
        var services = new ServiceCollection();

        services.AddApplication().AddInfrastructure(configuration).AddApi(configuration)
            .AddObservability(configuration);
        services.AddApplication().AddInfrastructure(configuration).AddApi(configuration)
            .AddObservability(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();

        Assert.Single(provider.GetServices<TimeProvider>());
        Assert.Single(provider.GetServices<Shared.Abstractions.ICorrelationIdAccessor>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.MaintOrbitDbContext>());
    }

    [Fact]
    public void NoServiceWeOwn_IsRegisteredAsASingletonHoldingAScopedDependency()
    {
        // DI-4, checked structurally rather than by relying on ValidateScopes to be enabled.
        // A captive scoped service outlives its scope and is shared across every subsequent
        // request, which under multi-tenancy presents as correct behaviour rather than an error.
        var services = Compose();

        var scopedServiceTypes = services
            .Where(static descriptor => descriptor.Lifetime == ServiceLifetime.Scoped)
            .Select(static descriptor => descriptor.ServiceType)
            .ToHashSet();

        var captives = OwnedBy(services)
            .Where(static descriptor => descriptor.Lifetime == ServiceLifetime.Singleton)
            .Where(descriptor => descriptor.ImplementationType is not null)
            .SelectMany(descriptor => Constructor(descriptor.ImplementationType!)
                .Where(parameter => scopedServiceTypes.Contains(parameter.ParameterType))
                .Select(parameter =>
                    $"{descriptor.ImplementationType!.Name} (singleton) <- {parameter.ParameterType.Name} (scoped)"))
            .ToList();

        Assert.Empty(captives);
    }

    [Fact]
    public void OurRegistrations_AreNotBuiltFromConcreteTypes()
    {
        // DI-6: register against the abstraction so a caller cannot depend on an implementation
        // detail by accident. Concrete self-registration is permitted where the concrete type is
        // the contract — DbContext and the middleware the pipeline activates by type.
        var selfRegistered = OwnedBy(Compose())
            .Where(static descriptor => descriptor.ServiceType == descriptor.ImplementationType)
            .Select(static descriptor => descriptor.ServiceType.Name)
            .ToList();

        Assert.Equal(["MaintOrbitDbContext"], selfRegistered);
    }

    private static ParameterInfo[] Constructor(Type type) =>
        type.GetConstructors().OrderByDescending(static c => c.GetParameters().Length)
            .FirstOrDefault()?.GetParameters() ?? [];
}
