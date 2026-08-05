using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaintOrbit.ArchitectureTests;

/// <summary>
/// Rules that keep the identity subsystem's security disciplines from decaying quietly.
/// </summary>
/// <remarks>
/// Every rule here describes something the codebase already does. That is the point: each one is a
/// convention followed consistently across eleven milestones by hand, and a convention followed by
/// hand is one that holds until the milestone somebody is in a hurry.
/// <para>
/// These are the disciplines whose breach is <i>invisible at runtime</i>. A secret that starts
/// printing itself, an options class that stops being validated at startup, a real key committed
/// to a settings file — none of them fails a request, throws, or shows up in a log as anything
/// other than the leak itself. They are exactly the class of defect a build gate is for, and
/// exactly the class a code review misses, because the diff that introduces one looks ordinary.
/// </para>
/// </remarks>
public sealed class SecurityRuleTests
{
    /// <summary>What a type carrying secret material must print instead of itself.</summary>
    private const string Redaction = "[REDACTED]";

    [Fact]
    public void EverySecretBearingValueObject_RedactsItsStringForm()
    {
        // The leak this prevents needs no mistake beyond silence. A `record` gets a compiler-
        // generated ToString that prints every member, so a new value object wrapping a token
        // hash leaks it the moment anything interpolates it — a log line, an exception message,
        // a validation failure, a debugger watch window. Nothing fails; the value is simply
        // there, in the one place 05-security §9 says credentials must be "absent by
        // construction, not masked".
        //
        // Stated by invoking ToString on an uninitialized instance rather than by matching
        // source text: what matters is what the method returns, not that a file happens to
        // contain the word REDACTED. A member-printing ToString either returns the members or
        // throws on the null ones, and both are caught here.
        var offenders = new List<string>();

        foreach (var type in SecretBearingValueObjects())
        {
            string? printed;

            try
            {
                printed = RuntimeHelpers.GetUninitializedObject(type).ToString();
            }
            catch (Exception ex)
            {
                // A ToString that dereferences its members is a ToString that prints them.
                offenders.Add($"{type.Name}.ToString() threw {ex.GetType().Name}");
                continue;
            }

            if (!string.Equals(printed, Redaction, StringComparison.Ordinal))
            {
                offenders.Add($"{type.Name}.ToString() returned '{printed}', not '{Redaction}'");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheRedactionRule_HasTypesToCheck()
    {
        // A rule whose candidate set silently empties is a rule that passes forever. If the
        // namespace is renamed or the suffix convention changes, this fails and says so rather
        // than leaving the gate green over nothing.
        Assert.NotEmpty(SecretBearingValueObjects());
    }

    [Fact]
    public void EveryBoundOptionsType_IsValidatedOnStart()
    {
        // ValidateOnStart is what turns a misconfiguration into a host that refuses to start.
        // Without it the options object is validated lazily — on first resolution — so a
        // deployment with an unreadable signing key or a 16-byte data key starts, reports
        // healthy, serves traffic, and fails on somebody's first sign-in instead. ADR-0021 puts
        // authentication in the fail-closed column, and a control that only fails once a real
        // Employee is on the other end of it is not failing closed.
        //
        // Only chains that Bind configuration are covered. Framework option types configured in
        // code (ForwardedHeadersOptions) have nothing to validate against a file.
        var offenders = BackendLayout.SourceFiles
            .SelectMany(path => OptionsChains(path)
                .Where(static chain => chain.Contains(".Bind(", StringComparison.Ordinal))
                .Where(static chain => !chain.Contains(".ValidateOnStart()", StringComparison.Ordinal))
                .Select(chain => $"{Path.GetFileName(path)}: {Summarize(chain)}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoCommittedSettingsFile_CarriesASecretValue()
    {
        // The standing constraint on this codebase is that no secret goes in any file, only
        // placeholders — and appsettings files are committed, so a value left in one is a value
        // in the history permanently, recoverable long after it is "removed". The three current
        // files hold empty strings and an elided PEM, and this keeps it that way.
        //
        // A placeholder is recognised by being empty or by obviously not being the thing: an
        // ellipsis, an <angle-bracketed> name, or the word "example". Anything else under a
        // secret-shaped key is treated as real, because assuming otherwise is how a key ships.
        var offenders = new List<string>();

        foreach (var file in SettingsFiles())
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

            InspectForSecrets(document.RootElement, Path.GetFileName(file), string.Empty, offenders);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheSecretScan_HasFilesToRead()
    {
        Assert.NotEmpty(SettingsFiles());
    }

    [Fact]
    public void CryptographicAlgorithms_AreConstructedOnlyInInfrastructure()
    {
        // Which algorithm protects what is an infrastructure decision with a documented answer:
        // Argon2id for passwords, SHA-256 for high-entropy secrets, AES-256-GCM for envelopes
        // (09-encryption-strategy §3). A handler or an entity that reached for a primitive
        // directly would be making that choice somewhere the decision tree does not apply, and
        // the result — a fast hash over a password, say — is indistinguishable from correct code
        // until it matters.
        //
        // CryptographicOperations is deliberately not on this list. Zeroing a buffer and
        // comparing in fixed time are hygiene available to any layer that briefly holds secret
        // material; MfaChallengeVerifier zeroes a decrypted TOTP secret, which is the behaviour
        // this rule should encourage rather than push into Infrastructure.
        var algorithms = new Regex(
            @"\b(RandomNumberGenerator|SHA1|SHA256|SHA384|SHA512|MD5|HMAC\w*|RSA|ECDsa|Aes|AesGcm|Rfc2898DeriveBytes)\s*\.\s*(Create|HashData|GetBytes|Encrypt|Decrypt|Sign\w*|Verify\w*)|new\s+(AesGcm|HMAC\w*)\s*\(",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        var offenders = BackendLayout.SourceFiles
            .Where(static path => !path.Contains(
                $"{Path.DirectorySeparatorChar}MaintOrbit.Infrastructure{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(path => algorithms
                .Matches(CSharpSource.StripCommentsAndLiterals(File.ReadAllText(path)))
                .Select(match => $"{Path.GetFileName(path)} uses {match.Value.Trim()}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheAlgorithmRule_SeesInfrastructuresOwnUse()
    {
        // The counterpart to the rule above: if the pattern matched nothing anywhere, the rule
        // would be vacuous rather than satisfied. Infrastructure genuinely does construct these,
        // so finding them there proves the pattern works.
        var infrastructure = BackendLayout.SourceFiles
            .Where(static path => path.Contains(
                $"{Path.DirectorySeparatorChar}MaintOrbit.Infrastructure{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Count(static path => CSharpSource.StripCommentsAndLiterals(File.ReadAllText(path))
                .Contains("RandomNumberGenerator.GetBytes", StringComparison.Ordinal));

        Assert.True(infrastructure > 0, "Expected Infrastructure to draw its own randomness.");
    }

    /// <summary>
    /// Value objects that carry secret or secret-derived material, by naming convention.
    /// </summary>
    /// <remarks>
    /// Scoped to the <c>ValueObjects</c> namespace so the suffixes mean what they say. The
    /// <c>Token</c> suffix is the reason: <c>InvitationToken</c> wraps a live credential, while
    /// <c>RefreshToken</c> and <c>PasswordResetToken</c> are entities that deliberately hold only a
    /// hash and have nothing to redact.
    /// </remarks>
    private static IReadOnlyList<Type> SecretBearingValueObjects()
    {
        string[] suffixes = ["Hash", "Secret", "Envelope", "Token"];

        return [.. typeof(Domain.Modules.Identity.ValueObjects.PasswordHash).Assembly
            .GetTypes()
            .Where(static type => type.Namespace?.EndsWith(".ValueObjects", StringComparison.Ordinal) == true)
            .Where(static type => type is { IsClass: true, IsAbstract: false })
            .Where(type => suffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal)))
            .OrderBy(static type => type.Name, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> SettingsFiles() =>
        [.. Directory
            .EnumerateFiles(Path.Combine(BackendLayout.Root, "src"), "appsettings*.json",
                SearchOption.AllDirectories)
            .Where(static path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static void InspectForSecrets(
        JsonElement element, string file, string path, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    InspectForSecrets(
                        property.Value,
                        file,
                        path.Length == 0 ? property.Name : $"{path}:{property.Name}",
                        offenders);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    InspectForSecrets(item, file, path, offenders);
                }

                break;

            case JsonValueKind.String when IsSecretShaped(path) && !IsPlaceholder(element.GetString()):
                offenders.Add($"{file} sets a value for '{path}'");
                break;

            default:
                break;
        }
    }

    /// <summary>Whether a settings key names something that would be a secret if populated.</summary>
    private static bool IsSecretShaped(string path)
    {
        // The leaf name only. "Encryption:DataKeyVersion" is not a key, and "KeyPrefix" is a Redis
        // namespace — matching the whole path would flag both and teach the next reader to ignore
        // this rule.
        var leaf = path.Split(':')[^1];

        string[] secretNames =
        [
            "PrivateKeyPem", "DataKey", "Secret", "ClientSecret", "Password", "ApiKey",
            "AccessKey", "ConnectionString"
        ];

        return secretNames.Any(name => leaf.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // A connection string is the one secret-shaped setting with a legitimate non-empty form:
        // host and database are not secret, a password is. This reads the credential's value
        // rather than the presence of its key — a trailing "Password=" is the template telling a
        // developer where to put theirs, which is the opposite of a leak.
        if (value.Contains('=', StringComparison.Ordinal)
            && value.Contains(';', StringComparison.Ordinal))
        {
            return !value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static pair => pair.StartsWith("Password=", StringComparison.OrdinalIgnoreCase))
                .Any(static pair => pair["Password=".Length..].Length > 0);
        }

        return value.Contains("...", StringComparison.Ordinal)
               || value.Contains('<', StringComparison.Ordinal)
               || value.Contains("example", StringComparison.OrdinalIgnoreCase)
               || value.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Each <c>AddOptions&lt;T&gt;()</c> call with the fluent chain that follows it.
    /// </summary>
    /// <remarks>
    /// The chain runs to the statement's semicolon, which is what makes the rule readable as
    /// written rather than needing a syntax tree: these registrations are one statement each by
    /// convention, and a chain broken across statements would not be a chain.
    /// </remarks>
    private static IEnumerable<string> OptionsChains(string path)
    {
        var source = CSharpSource.StripCommentsAndLiterals(File.ReadAllText(path));
        var index = 0;

        while ((index = source.IndexOf("AddOptions<", index, StringComparison.Ordinal)) >= 0)
        {
            var end = source.IndexOf(';', index);

            if (end < 0)
            {
                yield break;
            }

            yield return source[index..end];
            index = end;
        }
    }

    private static string Summarize(string chain) =>
        new string([.. chain.Where(static c => c is not ('\n' or '\r'))])
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
}
