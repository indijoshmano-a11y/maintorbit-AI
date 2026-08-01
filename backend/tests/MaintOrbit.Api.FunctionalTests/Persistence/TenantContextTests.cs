using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers the ambient tenant context.
/// </summary>
/// <remarks>
/// The value this carries ends up in a PostgreSQL session variable that every row-level security
/// policy compares against. If it fails to flow — across an await, into a nested operation, out
/// of a scope that ended — the symptom is zero rows rather than an error, which reads as "no
/// data" and gets investigated as a bug in something else entirely.
/// </remarks>
public sealed class TenantContextTests
{
    private static readonly CompanyId Alpha = new(Guid.CreateVersion7());
    private static readonly CompanyId Beta = new(Guid.CreateVersion7());

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestJwtConfiguration.With(new Dictionary<string, string?>
            {
                ["Application:Name"] = "MaintOrbit AI",
                ["Application:PublicBaseUrl"] = "https://api.example.test",
                ["Cors:AllowCredentials"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                ["Persistence:ConnectionString"] =
                    "Host=localhost;Database=maintorbit_test;Username=maintorbit"
            }))
            .Build();

        var services = new ServiceCollection();
        services.AddApplication().AddInfrastructure(configuration)
            .AddApi(configuration).AddObservability(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static ITenantContext Resolve(ServiceProvider provider) =>
        provider.GetRequiredService<ITenantContext>();

    [Fact]
    public void TenantContext_ResolvesFromTheCompositionRoot()
    {
        using var provider = BuildProvider();

        Assert.NotNull(Resolve(provider));
    }

    [Fact]
    public void TenantContext_IsSingletonAcrossScopes()
    {
        // Must be injectable into the connection interceptor, which is not request-scoped, and
        // reachable from the Worker, which has no request scope at all (TC-5).
        using var provider = BuildProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ITenantContext>(),
            second.ServiceProvider.GetRequiredService<ITenantContext>());
    }

    [Fact]
    public void Current_IsNull_OutsideATenantScope()
    {
        using var provider = BuildProvider();

        Assert.Null(Resolve(provider).Current);
    }

    [Fact]
    public void Require_Throws_OutsideATenantScope()
    {
        // TC-3: a request never proceeds untenanted. This is the fail-closed call site.
        using var provider = BuildProvider();

        Assert.Throws<InvalidOperationException>(() => Resolve(provider).Require());
    }

    [Fact]
    public void BeginTenantScope_MakesTheCompanyAmbient()
    {
        using var provider = BuildProvider();
        var context = Resolve(provider);

        using (context.BeginTenantScope(Alpha))
        {
            Assert.Equal(Alpha, context.Current);
            Assert.Equal(Alpha, context.Require());
        }

        Assert.Null(context.Current);
    }

    [Fact]
    public void BeginTenantScope_RejectsAnEmptyCompany()
    {
        // An empty discriminator would be written to the session variable, match nothing, and be
        // indistinguishable from a Company that genuinely has no rows.
        using var provider = BuildProvider();

        Assert.Throws<ArgumentException>(
            () => Resolve(provider).BeginTenantScope(CompanyId.Empty));
    }

    [Fact]
    public void NestedScope_RestoresTheOuterCompany()
    {
        // The elevated paths of TC-6 process one Company at a time. Clearing on dispose instead
        // of restoring would drop the outer tenant mid-iteration.
        using var provider = BuildProvider();
        var context = Resolve(provider);

        using (context.BeginTenantScope(Alpha))
        {
            using (context.BeginTenantScope(Beta))
            {
                Assert.Equal(Beta, context.Current);
            }

            Assert.Equal(Alpha, context.Current);
        }
    }

    [Fact]
    public async Task TenantContext_SurvivesAnAwait()
    {
        // The reason for AsyncLocal. A tenant lost after the first await is a query issued with
        // no session variable — which returns zero rows rather than failing.
        using var provider = BuildProvider();
        var context = Resolve(provider);

        using (context.BeginTenantScope(Alpha))
        {
            await Task.Yield();
            await Task.Run(static () => { }).ConfigureAwait(true);

            Assert.Equal(Alpha, context.Current);
        }
    }

    [Fact]
    public async Task ConcurrentOperations_DoNotSeeEachOthersTenant()
    {
        // The failure this design exists to prevent, at its smallest scale: two operations in
        // flight at once must not observe one another's Company.
        using var provider = BuildProvider();
        var context = Resolve(provider);

        async Task<CompanyId?> Observe(CompanyId company)
        {
            using (context.BeginTenantScope(company))
            {
                await Task.Delay(5).ConfigureAwait(false);
                return context.Current;
            }
        }

        var results = await Task.WhenAll(Observe(Alpha), Observe(Beta)).ConfigureAwait(true);

        Assert.Equal(Alpha, results[0]);
        Assert.Equal(Beta, results[1]);
    }
}
