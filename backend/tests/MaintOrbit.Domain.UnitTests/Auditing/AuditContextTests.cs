using MaintOrbit.Domain.Modules.Auditing;
using AuditEvent = MaintOrbit.Domain.Modules.Auditing.Entities.AuditEvent;
using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Domain.UnitTests.Auditing;

/// <summary>
/// Covers the guard on what may enter an Audit Event's context.
/// </summary>
/// <remarks>
/// <b>The audit store is the only one with no delete path.</b> Everything else has retention, a
/// purge, or a soft-delete flag; audit rows are append-only by design, kept twelve months or more,
/// and exported to customers. A credential that lands here cannot be removed by any code the
/// system has — AU-1 removed those paths deliberately. That asymmetry is what makes this guard
/// worth testing at the level of individual key spellings.
/// </remarks>
public sealed class AuditContextTests
{
    private static readonly DateTimeOffset Occurred =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("newPassword")]
    [InlineData("passwordHash")]
    [InlineData("password_hash")]
    [InlineData("accessToken")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("refresh_token")]
    [InlineData("token")]
    [InlineData("tokenHash")]
    [InlineData("Authorization")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("clientSecret")]
    [InlineData("client_secret")]
    [InlineData("secret")]
    [InlineData("privateKeyPem")]
    [InlineData("Cookie")]
    [InlineData("totpSeed")]
    [InlineData("recoveryCode")]
    [InlineData("salt")]
    [InlineData("signature")]
    [InlineData("otp")]
    public void ACredentialShapedKey_IsRedacted(string key)
    {
        var sanitized = AuditContext.Sanitize(
            new Dictionary<string, string>(StringComparer.Ordinal) { [key] = "correct-horse" });

        Assert.Equal(AuditContext.Redacted, sanitized![key]);
        Assert.DoesNotContain("correct-horse", sanitized[key], StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionSurvivesRecording()
    {
        // The guard runs inside the factory, not at the call site. Thirteen emission points exist
        // today and more arrive with every module; a convention applied at each is one that holds
        // until somebody adds the next.
        var recorded = AuditEvent.Record(
            Occurred,
            AuditActions.SignIn,
            AuditOutcome.Success,
            AuditActorType.System,
            context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["password"] = "correct horse battery staple",
                ["clientType"] = "WebConsole"
            });

        Assert.Equal(AuditContext.Redacted, recorded.Context!["password"]);
        Assert.Equal("WebConsole", recorded.Context["clientType"]);
    }

    [Theory]
    [InlineData("clientType")]
    [InlineData("attemptedEmail")]
    [InlineData("roleCode")]
    [InlineData("revokedCount")]
    [InlineData("permission")]
    public void AnOrdinaryDiagnosticKey_IsKept(string key)
    {
        // The guard has to leave the trail useful. Every key here is one an emission point already
        // writes, so a change that started redacting them would empty the records of their detail.
        var sanitized = AuditContext.Sanitize(
            new Dictionary<string, string>(StringComparer.Ordinal) { [key] = "value" });

        Assert.Equal("value", sanitized![key]);
    }

    [Fact]
    public void ALongValue_IsTruncatedRatherThanStored()
    {
        // AU-4 forbids prompt and completion content, and §8.5 expects small scalars. A cap is the
        // structural way to say that: a request body or a completion does not fit, so the column
        // cannot quietly become a general-purpose payload.
        var sanitized = AuditContext.Sanitize(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["body"] = new('x', AuditContext.MaximumValueLength * 3)
            });

        Assert.EndsWith(AuditContext.Truncated, sanitized!["body"], StringComparison.Ordinal);
        Assert.Equal(
            AuditContext.MaximumValueLength + AuditContext.Truncated.Length,
            sanitized["body"].Length);
    }

    [Fact]
    public void AValueAtTheCap_IsUntouched()
    {
        var value = new string('x', AuditContext.MaximumValueLength);

        var sanitized = AuditContext.Sanitize(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["note"] = value });

        Assert.Equal(value, sanitized!["note"]);
    }

    [Fact]
    public void AnEmptyContext_BecomesNothing()
    {
        // A JSONB column holding `{}` on every row costs storage on the largest table in the
        // system and tells a reader nothing.
        Assert.Null(AuditContext.Sanitize(new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Null(AuditContext.Sanitize(null));
    }

    [Fact]
    public void ANamelessEntry_IsDropped()
    {
        var sanitized = AuditContext.Sanitize(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["  "] = "orphan",
                ["kept"] = "value"
            });

        Assert.False(sanitized!.ContainsKey("  "));
        Assert.Single(sanitized);
    }

    [Fact]
    public void TheStoredContext_DoesNotChangeWhenTheCallersDictionaryDoes()
    {
        // Sanitize copies rather than wrapping. An audit record that changed after it was written
        // would defeat the point of the store it goes into — and a caller reusing a dictionary
        // across emissions is an ordinary thing to do.
        var caller = new Dictionary<string, string>(StringComparer.Ordinal) { ["state"] = "before" };

        var recorded = AuditEvent.Record(
            Occurred, AuditActions.SignIn, AuditOutcome.Success, AuditActorType.System,
            context: caller);

        caller["state"] = "after";

        Assert.Equal("before", recorded.Context!["state"]);
    }

    [Fact]
    public void ABooleanFlagUnderACredentialShapedKey_IsKept()
    {
        // usedRecoveryCode is what the MFA challenge records — a flag, not a code — and §3.4 wants
        // it: a run of recovery-code authentications is somebody who has lost their authenticator.
        // A key-name rule alone redacted it and destroyed the signal, which is why the exemption
        // exists at all.
        var sanitized = AuditContext.Sanitize(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["usedRecoveryCode"] = bool.TrueString
            });

        Assert.Equal(bool.TrueString, sanitized!["usedRecoveryCode"]);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("0")]
    [InlineData("8fj3kd0slam2")]
    public void ANonBooleanUnderACredentialShapedKey_IsStillRedacted(string value)
    {
        // The exemption is booleans only. A PIN, a one-time code, and a truncated key can all look
        // like a small number; none of them can look like True or False.
        var sanitized = AuditContext.Sanitize(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["recoveryCode"] = value });

        Assert.Equal(AuditContext.Redacted, sanitized!["recoveryCode"]);
    }

    [Fact]
    public void TheRuleIsStatedOnce()
    {
        // The predicate is public so a caller choosing a context key can check their own spelling
        // against the same list the guard uses, rather than against a copy of it.
        Assert.True(AuditContext.IsCredentialShaped("refreshToken"));
        Assert.False(AuditContext.IsCredentialShaped("clientType"));
    }
}
