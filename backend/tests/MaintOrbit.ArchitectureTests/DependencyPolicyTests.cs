namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// AT-12 and the package management rules.
/// </summary>
/// <remarks>
/// NFR-PORT-002 — no dependency that cannot run in a customer-controlled environment — is
/// described in the requirements as the constraint with the longest reach: inexpensive to honour
/// from the start and extremely expensive to retrofit. A cloud-coupled package added casually is
/// not visible in any behaviour until self-hosting is attempted, by which point it is a
/// re-architecture rather than a dependency swap.
/// </remarks>
public sealed class DependencyPolicyTests
{
    /// <summary>Package prefixes that bind a deployment to one vendor's cloud.</summary>
    private static readonly string[] CloudCoupledPrefixes =
    [
        "Azure.",
        "Microsoft.Azure.",
        "Microsoft.ApplicationInsights",
        "AWSSDK.",
        "Amazon.",
        "Google.Cloud."
    ];

    [Fact]
    public void NoLayerOutsideInfrastructure_ReferencesACloudCoupledPackage()
    {
        // ADR-0017 permits a cloud-coupled package only behind a port, and a port's adapter lives
        // in Infrastructure. Anywhere else and the coupling has already escaped the seam that was
        // supposed to contain it.
        var violations = BackendLayout.AllProjects
            .Where(static project => project.Name != "MaintOrbit.Infrastructure")
            .SelectMany(static project => project.PackageReferences
                .Where(static package => CloudCoupledPrefixes.Any(
                    prefix => package.StartsWith(prefix, StringComparison.Ordinal)))
                .Select(package => $"{project.Name}: {package}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void NoProject_PinsItsOwnPackageVersion()
    {
        // PM-2. Central Package Management exists so a version is stated once. A local
        // Version= overrides it silently, which is how two projects end up on different
        // versions of the same package and the difference is found from a runtime type
        // mismatch rather than from the manifest.
        var violations = BackendLayout.AllProjects
            .Where(static project => project.Text.Contains(
                "PackageReference", StringComparison.Ordinal))
            .Where(static project => project.Text.Contains(
                "Version=\"", StringComparison.Ordinal))
            .Select(static project => project.Name)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryReferencedPackage_HasACentrallyDeclaredVersion()
    {
        // The build fails on a missing PackageVersion, so this is a second reading of the same
        // rule — but it also names the gap, which the build error does not do well when several
        // are missing at once.
        var declared = File.ReadAllText(Path.Combine(BackendLayout.Root, "Directory.Packages.props"));

        var missing = BackendLayout.AllProjects
            .SelectMany(static project => project.PackageReferences)
            .Distinct(StringComparer.Ordinal)
            .Where(package => !declared.Contains($"Include=\"{package}\"", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void CentralPackageManagement_IsEnabled()
    {
        var props = File.ReadAllText(Path.Combine(BackendLayout.Root, "Directory.Packages.props"));

        Assert.Contains(
            "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
            props,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProduct_TargetsTheSupportedRuntime()
    {
        // TD-1 records that the originally stated runtime is out of support. One project left on
        // a different target framework would be the one that stops receiving security fixes, and
        // nothing about the build would say so.
        var props = File.ReadAllText(Path.Combine(BackendLayout.Root, "Directory.Build.props"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", props, StringComparison.Ordinal);

        var overrides = BackendLayout.AllProjects
            .Where(static project => project.Text.Contains("<TargetFramework", StringComparison.Ordinal))
            .Select(static project => project.Name)
            .ToList();

        Assert.Empty(overrides);
    }

    [Fact]
    public void WarningsAreErrors_AndAnalysisIsEnforced()
    {
        // Every rule in this suite is a build gate only because the build refuses to produce a
        // binary with warnings. Turning that off would silently downgrade the analyzers that
        // enforce CA1848, CA1873, and the nullable contract across the codebase.
        var props = File.ReadAllText(Path.Combine(BackendLayout.Root, "Directory.Build.props"));

        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", props, StringComparison.Ordinal);
        Assert.Contains("<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>", props, StringComparison.Ordinal);
        Assert.Contains("<Nullable>enable</Nullable>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzerSuppressions_AreScopedToTestProjects()
    {
        // CA1707 and CA1848 are suppressed, both for stated reasons and both only where
        // IsTestProject is true. If either moved into Directory.Build.props it would apply to
        // production code — CA1848 in particular, which is what keeps allocating log calls off
        // the request path.
        var props = File.ReadAllText(Path.Combine(BackendLayout.Root, "Directory.Build.props"));
        var targets = File.ReadAllText(Path.Combine(BackendLayout.Root, "Directory.Build.targets"));

        Assert.DoesNotContain("CA1848", props, StringComparison.Ordinal);
        Assert.DoesNotContain("CA1707", props, StringComparison.Ordinal);
        Assert.Contains("'$(IsTestProject)' == 'true'", targets, StringComparison.Ordinal);
        Assert.Contains("CA1848", targets, StringComparison.Ordinal);
    }
}
