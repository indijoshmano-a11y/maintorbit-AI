namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// AT-9 — the ambient clock is not read directly anywhere in the source.
/// </summary>
/// <remarks>
/// U-3 forbids <c>DateTime.Now</c> and <c>DateTime.UtcNow</c> outside the time abstraction, and
/// database-design §1.7 requires every timestamp to come through it rather than from <c>now()</c>
/// scattered through the code. The reason is testability: a rule about expiry, retention, or a
/// billing period that reads the machine clock cannot be tested without waiting, so in practice
/// it is not tested at all.
/// <para>
/// <c>DateTime.Now</c> carries a second problem. It returns local time, and every timestamp in
/// this system is UTC (FR-X-003). One local-time value written to a <c>timestamptz</c> column is
/// silently wrong by the host's offset.
/// </para>
/// </remarks>
public sealed class TimeAbstractionTests
{
    /// <summary>Members that read the ambient clock.</summary>
    private static readonly string[] AmbientClockMembers =
    [
        "DateTime.Now",
        "DateTime.UtcNow",
        "DateTime.Today",
        "DateTimeOffset.Now",
        "DateTimeOffset.UtcNow",
        "Stopwatch.GetTimestamp",
        "Environment.TickCount"
    ];

    [Fact]
    public void NoSourceFile_ReadsTheAmbientClock()
    {
        var violations = new List<string>();

        foreach (var file in BackendLayout.SourceFiles)
        {
            var code = CSharpSource.StripCommentsAndLiterals(File.ReadAllText(file));

            violations.AddRange(AmbientClockMembers
                .Where(member => code.Contains(member, StringComparison.Ordinal))
                .Select(member => $"{Path.GetFileName(file)}: {member}"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void TheRuleIsCheckedAgainstRealSource()
    {
        // Guards the rule rather than the code. A path change or a build layout change would
        // leave the scan examining nothing and passing silently, which is the failure mode every
        // file-walking test has.
        Assert.NotEmpty(BackendLayout.SourceFiles);
        Assert.Contains(
            BackendLayout.SourceFiles,
            static file => file.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void TimeProviderIsRegistered_SoThereIsSomethingToUseInstead()
    {
        // A prohibition with no alternative gets worked around. TimeProvider is the abstraction
        // U-3 points at, and it is registered in the infrastructure composition root.
        var registration = BackendLayout.SourceFiles
            .Single(static file => file.EndsWith("InfrastructureServiceCollectionExtensions.cs", StringComparison.Ordinal));

        Assert.Contains(
            "TimeProvider.System",
            File.ReadAllText(registration),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Stripper_IgnoresProseButNotCode()
    {
        // The scan above is only trustworthy if the stripper is. This codebase documents U-3 in
        // an XML comment that names the forbidden member, so a stripper that missed comments
        // would fail the build on its own documentation.
        const string Source = """
            /// <remarks>U-3 forbids DateTime.UtcNow outside the abstraction.</remarks>
            // TODO: replace DateTime.Now here
            /* DateTime.Today */
            var message = "DateTime.UtcNow";
            var actual = SomeType.Value;
            """;

        var stripped = CSharpSource.StripCommentsAndLiterals(Source);

        Assert.DoesNotContain("DateTime", stripped, StringComparison.Ordinal);
        Assert.Contains("SomeType.Value", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Stripper_LeavesRealUsageVisible()
    {
        var stripped = CSharpSource.StripCommentsAndLiterals(
            "var now = DateTime.UtcNow; // reads the machine clock");

        Assert.Contains("DateTime.UtcNow", stripped, StringComparison.Ordinal);
    }
}
