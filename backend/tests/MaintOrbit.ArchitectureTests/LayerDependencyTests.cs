namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// Enforces the Clean Architecture dependency rule (ADR-0001).
/// </summary>
/// <remarks>
/// ADR-0001 is a convention until something checks it. These are the checks: dependencies point
/// inward, and the graph is acyclic. A violation is cheap to introduce — one
/// <c>ProjectReference</c> added to make a compile error go away — and expensive to reverse once
/// code has been written against it, because by then the inversion is load-bearing.
/// </remarks>
public sealed class LayerDependencyTests
{
    private const string Domain = "MaintOrbit.Domain";
    private const string Application = "MaintOrbit.Application";
    private const string Infrastructure = "MaintOrbit.Infrastructure";
    private const string Api = "MaintOrbit.Api";
    private const string Shared = "MaintOrbit.Shared";

    /// <summary>What each layer is permitted to reference. Anything absent is a violation.</summary>
    private static readonly Dictionary<string, string[]> Permitted = new(StringComparer.Ordinal)
    {
        [Shared] = [],
        [Domain] = [Shared],
        [Application] = [Domain, Shared],
        [Infrastructure] = [Application, Domain, Shared],
        [Api] = [Application, Infrastructure, Shared]
    };

    [Fact]
    public void EveryLayer_ReferencesOnlyWhatItIsPermittedTo()
    {
        var violations = new List<string>();

        foreach (var (layer, allowed) in Permitted)
        {
            var actual = BackendLayout.Project(layer).ProjectReferences;

            violations.AddRange(actual
                .Except(allowed, StringComparer.Ordinal)
                .Select(forbidden => $"{layer} -> {forbidden}"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Shared_ReferencesNothing()
    {
        // The shared kernel is referenced by every layer including Domain. A reference out of it
        // would reach every layer at once and would make the innermost layer depend on whatever
        // it pointed at.
        Assert.Empty(BackendLayout.Project(Shared).ProjectReferences);
    }

    [Fact]
    public void Domain_ReferencesOnlyShared()
    {
        // AT-1.
        Assert.Equal([Shared], BackendLayout.Project(Domain).ProjectReferences);
    }

    [Fact]
    public void Application_DoesNotReferenceInfrastructure()
    {
        // AT-2, and the dependency inversion the whole architecture turns on. Application
        // declares ports; Infrastructure implements them. Reversing this arrow makes the
        // business rules depend on the database driver, which is the state ADR-0001 exists to
        // prevent.
        Assert.DoesNotContain(
            Infrastructure,
            BackendLayout.Project(Application).ProjectReferences,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Api_IsTheOutermostLayer()
    {
        // Nothing may reference the host. A layer that did would be reaching outward for
        // transport concerns, and would drag HTTP into code that must remain callable from the
        // Worker.
        var referencingApi = BackendLayout.SourceProjects
            .Where(project => project.Name != Api)
            .Where(project => project.ProjectReferences.Contains(Api, StringComparer.Ordinal))
            .Select(static project => project.Name)
            .ToList();

        Assert.Empty(referencingApi);
    }

    [Fact]
    public void DependencyGraph_IsAcyclic()
    {
        // A cycle makes the layers one unit: nothing can be reasoned about, tested, or extracted
        // in isolation. The project system rejects cycles between projects, so this guards the
        // rule for the module-level graph that AT-3 will police once modules exist.
        var graph = BackendLayout.SourceProjects.ToDictionary(
            static project => project.Name,
            static project => project.ProjectReferences,
            StringComparer.Ordinal);

        var cycles = FindCycles(graph);

        Assert.Empty(cycles);
    }

    [Fact]
    public void EveryLayerInTheDependencyRule_Exists()
    {
        // Guards the tests above rather than the architecture. Every rule here is stated against
        // a project looked up by name, so a renamed or removed project would make them pass by
        // examining nothing.
        var names = BackendLayout.SourceProjects.Select(static project => project.Name).ToList();

        Assert.Equal(Permitted.Keys.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }

    private static List<string> FindCycles(Dictionary<string, IReadOnlyList<string>> graph)
    {
        var cycles = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in graph.Keys)
        {
            Visit(node, []);
        }

        return cycles;

        void Visit(string node, List<string> path)
        {
            if (state.TryGetValue(node, out var seen))
            {
                if (seen == 1)
                {
                    cycles.Add(string.Join(" -> ", [.. path, node]));
                }

                return;
            }

            state[node] = 1;

            if (graph.TryGetValue(node, out var edges))
            {
                foreach (var edge in edges)
                {
                    Visit(edge, [.. path, node]);
                }
            }

            state[node] = 2;
        }
    }
}
