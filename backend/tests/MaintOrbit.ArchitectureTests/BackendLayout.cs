using System.Xml.Linq;

namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// Reads the backend's project files so rules can be stated against the real dependency graph.
/// </summary>
/// <remarks>
/// Project files rather than compiled assemblies are the primary source here, deliberately. The
/// compiler elides a project reference that is declared but never used, so an assembly's
/// reference list answers "what does this code use" while the project file answers "what is this
/// project allowed to use". The architecture rules are about permission, so the declaration is
/// what they are stated against — a reference added in anticipation is a boundary violation the
/// day it is written, not the day something finally calls through it.
/// <para>
/// Rules are implemented over reflection and file inspection rather than
/// <c>NetArchTest</c> or <c>ArchUnitNET</c>. <c>docs/04-technology/backend-technologies.md</c>
/// §12 anticipates exactly this — "rules are simple enough to reimplement over reflection APIs;
/// the <i>rules</i> are the asset, not the library". Two things made it the better trade here:
/// the current <c>NetArchTest.Rules</c> release targets netstandard2.0 and carries a 2020-era
/// Mono.Cecil, and neither library covers AT-9 or AT-12, which are about source usage and
/// package manifests rather than type graphs.
/// </para>
/// </remarks>
internal static class BackendLayout
{
    /// <summary>The <c>backend/</c> directory.</summary>
    public static string Root { get; } = FindBackendRoot();

    /// <summary>Every project under <c>backend/src</c>.</summary>
    public static IReadOnlyList<ProjectFile> SourceProjects { get; } = Load("src");

    /// <summary>Every project under <c>backend/tests</c>.</summary>
    public static IReadOnlyList<ProjectFile> TestProjects { get; } = Load("tests");

    /// <summary>Every project in the backend.</summary>
    public static IReadOnlyList<ProjectFile> AllProjects { get; } =
        [.. SourceProjects, .. TestProjects];

    /// <summary>Every <c>.cs</c> file under <c>backend/src</c>.</summary>
    public static IReadOnlyList<string> SourceFiles { get; } =
        [.. Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsGenerated(path))
            .Order(StringComparer.Ordinal)];

    public static ProjectFile Project(string name) =>
        AllProjects.SingleOrDefault(p => p.Name == name)
        ?? throw new InvalidOperationException(
            $"No project named '{name}'. Found: {string.Join(", ", AllProjects.Select(static p => p.Name))}.");

    /// <summary>
    /// Walks upward for the solution file.
    /// </summary>
    /// <remarks>
    /// The test binary runs from <c>bin/{configuration}/{tfm}</c>, whose depth is not fixed
    /// across configurations. Searching for a known marker survives that, and fails loudly
    /// rather than silently examining an empty file set if the layout changes.
    /// </remarks>
    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MaintOrbit.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"MaintOrbit.sln not found above '{AppContext.BaseDirectory}'. " +
            "The architecture tests read project files from disk and cannot run without it.");
    }

    private static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static IReadOnlyList<ProjectFile> Load(string folder)
    {
        var root = Path.Combine(Root, folder);

        return [.. Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(ProjectFile.Read)
            .OrderBy(static project => project.Name, StringComparer.Ordinal)];
    }
}

/// <summary>
/// One project file, reduced to what the architecture rules ask about.
/// </summary>
internal sealed record ProjectFile(
    string Name,
    string Path,
    string Sdk,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences)
{
    public static ProjectFile Read(string path)
    {
        var document = XDocument.Load(path);
        var root = document.Root
            ?? throw new InvalidOperationException($"'{path}' has no root element.");

        return new ProjectFile(
            Name: System.IO.Path.GetFileNameWithoutExtension(path),
            Path: path,
            Sdk: root.Attribute("Sdk")?.Value ?? string.Empty,
            ProjectReferences: [.. Attributes(root, "ProjectReference", "Include")
                .Select(static include => System.IO.Path.GetFileNameWithoutExtension(
                    include.Replace('\\', System.IO.Path.DirectorySeparatorChar)))],
            PackageReferences: [.. Attributes(root, "PackageReference", "Include")],
            FrameworkReferences: [.. Attributes(root, "FrameworkReference", "Include")]);
    }

    /// <summary>Raw text, for rules about how the file is written rather than what it declares.</summary>
    public string Text => File.ReadAllText(Path);

    private static IEnumerable<string> Attributes(XElement root, string element, string attribute) =>
        root.Descendants(element)
            .Select(node => node.Attribute(attribute)?.Value)
            .Where(static value => !string.IsNullOrEmpty(value))
            .Select(static value => value!);
}
