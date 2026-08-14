namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// Rules that keep authorization declarative.
/// </summary>
/// <remarks>
/// These become worth enforcing the moment a permission actually gates an endpoint. Before that
/// the model was infrastructure nothing depended on; now every endpoint added from here on decides
/// whether the system stays permission-based or drifts back into scattered checks.
/// <para>
/// Stated over source text rather than a type graph, because both rules are about how something is
/// written — a literal in a call, and a name in a comparison — which reflection cannot see.
/// Comments and string literals are stripped first, so the documentation that explains a rule does
/// not violate it (that is what <see cref="CSharpSource"/> exists for)... except where the rule is
/// <i>about</i> literals, in which case the raw text is what must be read.
/// </para>
/// </remarks>
public sealed class AuthorizationRuleTests
{
    /// <summary>The files allowed to name a permission — one catalogue per module.</summary>
    /// <remarks>
    /// A permission belongs to the module whose resource it governs, so the catalogue is per
    /// module rather than global. The property this rule protects is unchanged: a code is written
    /// once, so a misspelling is a compile error instead of a policy that silently denies
    /// everybody. <c>NoCodeIsNamedTwice</c> keeps the catalogues from overlapping.
    /// </remarks>
    private static readonly string[] Catalogues =
        ["IdentityPermissions.cs", "AuditPermissions.cs"];

    [Fact]
    public void PermissionCodesAreNamedInExactlyOnePlace()
    {
        // A permission spelled out at its call site is a permission that can be misspelled there.
        // Under deny-by-default a typo is not an error — it is a policy that silently refuses
        // everybody, which reads as "authorization is broken" rather than as a one-character bug.
        //
        // Concentrating the names means a typo is a compile error, and it also makes the answer to
        // "what does this deployment enforce?" a single file rather than a search.
        var offenders = BackendLayout.SourceFiles
            .Where(static path => !Catalogues.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Where(static path => File.ReadAllText(path).Contains(
                "PermissionCode.Create(\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoPermissionCodeIsNamedTwice()
    {
        // The catalogues partition the permissions; they must not overlap. Two constants with the
        // same value would let one be changed while callers of the other kept the old policy.
        var codes = BackendLayout.SourceFiles
            .Where(static path => Catalogues.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .SelectMany(static path => System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(path), @"PermissionCode\.Create\(""(?<code>[^""]+)""\)")
                .Select(match => match.Groups["code"].Value))
            .ToList();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NothingBranchesOnARoleName()
    {
        // SD-020 makes roles presets over permissions, and CLAUDE.md §9 lists branching
        // authorization on a role name among the things never to do. A role conditional is what
        // turns FR-PERM-006's custom roles from a data change into a rewrite — AU-007 and risk R-8
        // both say so.
        //
        // The seven documented role codes, as they would appear in a comparison.
        string[] roleNames =
        [
            "company-owner", "company-admin", "billing-admin", "team-lead",
            "developer", "analyst", "auditor"
        ];

        var offenders = BackendLayout.SourceFiles
            .Select(path => (Path.GetFileName(path), Source: File.ReadAllText(path)))
            .SelectMany(file => roleNames
                .Where(role => file.Source.Contains($"\"{role}\"", StringComparison.Ordinal))
                .Select(role => $"{file.Item1} names the role '{role}'"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void OnlyTheApiAuthorizationFolderKnowsAboutPolicies()
    {
        // ASP.NET Core's policy machinery is an outermost-layer detail. A handler or a repository
        // that reached for it would be deciding authorization somewhere the pipeline cannot see,
        // and ADR-0001 puts framework concerns in the layer that owns the framework.
        var offenders = BackendLayout.SourceFiles
            .Where(static path => !path.Contains(
                Path.Combine("MaintOrbit.Api", "Authorization"), StringComparison.Ordinal))
            .Where(static path => !path.Contains(
                Path.Combine("MaintOrbit.Api", "Authentication"), StringComparison.Ordinal))
            .Where(static path =>
            {
                var source = CSharpSource.StripCommentsAndLiterals(File.ReadAllText(path));

                return source.Contains("IAuthorizationRequirement", StringComparison.Ordinal)
                       || source.Contains("AuthorizationPolicyBuilder", StringComparison.Ordinal)
                       || source.Contains("IAuthorizationPolicyProvider", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
