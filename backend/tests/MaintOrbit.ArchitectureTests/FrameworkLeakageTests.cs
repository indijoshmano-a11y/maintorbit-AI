using System.Reflection;

namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// Keeps transport and persistence technology out of the inner layers.
/// </summary>
/// <remarks>
/// The project reference graph is only half the dependency rule. A layer can also be pulled
/// outward by a NuGet package: EF Core in the Application layer would let a handler write a query,
/// and ASP.NET Core in the Domain would let an entity know about HTTP. Neither shows up as a
/// project reference, and both are exactly what ADR-0001 forbids.
/// <para>
/// Checked against both the package manifest and the compiled assembly. The manifest catches a
/// declared dependency; the assembly catches one arriving transitively, which is the case nobody
/// notices.
/// </para>
/// </remarks>
public sealed class FrameworkLeakageTests
{
    /// <summary>Layers that must not know about transport or persistence technology.</summary>
    private static readonly string[] InnerLayers =
        ["MaintOrbit.Shared", "MaintOrbit.Domain", "MaintOrbit.Application"];

    /// <summary>Assembly and package prefixes that carry an outer-layer concern.</summary>
    private static readonly string[] ForbiddenInward =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Microsoft.Extensions.Hosting"
    ];

    [Theory]
    [InlineData("MaintOrbit.Shared")]
    [InlineData("MaintOrbit.Domain")]
    [InlineData("MaintOrbit.Application")]
    public void InnerLayer_DeclaresNoOuterLayerPackage(string layer)
    {
        var forbidden = BackendLayout.Project(layer).PackageReferences
            .Where(static package => ForbiddenInward.Any(
                prefix => package.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(forbidden);
    }

    [Theory]
    [InlineData("MaintOrbit.Shared")]
    [InlineData("MaintOrbit.Domain")]
    [InlineData("MaintOrbit.Application")]
    public void InnerLayer_LinksNoOuterLayerAssembly(string layer)
    {
        // Catches what the manifest cannot: a forbidden assembly arriving through a permitted
        // package's own dependencies and then being used.
        var referenced = Assembly.Load(new AssemblyName(layer))
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name => ForbiddenInward.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(referenced);
    }

    [Theory]
    [InlineData("MaintOrbit.Shared")]
    [InlineData("MaintOrbit.Domain")]
    [InlineData("MaintOrbit.Application")]
    [InlineData("MaintOrbit.Infrastructure")]
    public void OnlyTheHost_UsesTheWebSdk(string layer)
    {
        // The Web SDK adds an implicit framework reference to Microsoft.AspNetCore.App, which
        // would make the whole ASP.NET Core surface available to a layer that must stay callable
        // from the Worker host. It is the quietest way to lose the boundary.
        var project = BackendLayout.Project(layer);

        Assert.Equal("Microsoft.NET.Sdk", project.Sdk);
        Assert.Empty(project.FrameworkReferences);
    }

    [Fact]
    public void Domain_HasNoPackageDependenciesAtAll()
    {
        // The innermost layer holds business rules and nothing else. Every package added here
        // becomes something the rules are expressed in terms of, and therefore something that
        // has to be carried into any future extraction.
        Assert.Empty(BackendLayout.Project("MaintOrbit.Domain").PackageReferences);
    }

    [Fact]
    public void Infrastructure_IsTheOnlyLayerHoldingPersistencePackages()
    {
        // ADR-0023 puts EF Core behind the ports the Application declares. Persistence packages
        // anywhere else mean a query can be written outside the adapter, which is how tenant
        // filtering gets bypassed — the interceptor coverage ADR-0023 calls decisive only works
        // if every query goes through the context it configures.
        var holders = BackendLayout.SourceProjects
            .Where(static project => project.PackageReferences.Any(static package =>
                package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || package.StartsWith("Npgsql", StringComparison.Ordinal)))
            .Select(static project => project.Name)
            .ToList();

        Assert.Equal(["MaintOrbit.Infrastructure"], holders);
    }
}
