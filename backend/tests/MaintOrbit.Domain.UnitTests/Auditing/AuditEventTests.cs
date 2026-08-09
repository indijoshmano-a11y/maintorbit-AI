using System.Reflection;
using MaintOrbit.Domain.Modules.Auditing;
using AuditEvent = MaintOrbit.Domain.Modules.Auditing.Entities.AuditEvent;
using MaintOrbit.Shared.Auditing;
using MaintOrbit.Shared.MultiTenancy;

namespace MaintOrbit.Domain.UnitTests.Auditing;

/// <summary>
/// Covers the Audit Event aggregate.
/// </summary>
/// <remarks>
/// The immutability tests here are structural rather than behavioural, and deliberately so. AU-1
/// requires that no update path exists <i>in code</i> — so the thing worth asserting is the shape
/// of the type, not the outcome of calling a mutator that should not exist to be called.
/// </remarks>
public sealed class AuditEventTests
{
    private static readonly DateTimeOffset Occurred =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARecordedEvent_CarriesWhatAu3Requires()
    {
        // AU-3: actor, action, target, outcome, timestamp, originating context.
        var company = new CompanyId(Guid.CreateVersion7());
        var actor = Guid.CreateVersion7();

        var recorded = AuditEvent.Record(
            Occurred,
            AuditActions.RoleAssigned,
            AuditOutcome.Success,
            AuditActorType.Employee,
            company,
            actor,
            AuditTargets.RoleAssignment,
            "assignment-1",
            "correlation-1",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["roleCode"] = "analyst" });

        Assert.Equal(Occurred, recorded.OccurredAtUtc);
        Assert.Equal(AuditActions.RoleAssigned, recorded.Action);
        Assert.Equal(AuditOutcome.Success, recorded.Outcome);
        Assert.Equal(AuditActorType.Employee, recorded.ActorType);
        Assert.Equal(company, recorded.CompanyId);
        Assert.Equal(actor, recorded.ActorEmployeeId);
        Assert.Equal(AuditTargets.RoleAssignment, recorded.TargetType);
        Assert.Equal("assignment-1", recorded.TargetId);
        Assert.Equal("correlation-1", recorded.CorrelationId);
        Assert.Equal("analyst", recorded.Context!["roleCode"]);
        Assert.False(recorded.Id.IsEmpty);
    }

    [Fact]
    public void EachEvent_GetsATimeOrderedIdentifier()
    {
        // UUIDv7 (§1.6). On a write-heavy partitioned relation the ordering is what keeps inserts
        // at the right edge of the index rather than scattered through it (§9.4).
        var first = Recorded();
        var second = Recorded();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(7, first.Id.Value.Version);
    }

    [Fact]
    public void AnEventWithoutAnAction_IsRefused()
    {
        // An append-only row that does not say what happened is useless and permanent.
        Assert.Throws<ArgumentException>(() => AuditEvent.Record(
            Occurred, "  ", AuditOutcome.Success, AuditActorType.System));
    }

    [Fact]
    public void AnEventWithoutATimestamp_IsRefused()
    {
        // The partition key. An unset value routes the row to a partition that does not exist,
        // and the insert fails a long way from the cause.
        Assert.Throws<ArgumentException>(() => AuditEvent.Record(
            default, AuditActions.SignIn, AuditOutcome.Success, AuditActorType.System));
    }

    [Fact]
    public void AnEmployeeActorWithoutAnIdentifier_IsRefused()
    {
        // A record claiming an Employee acted while naming none contradicts itself, and is
        // unattributable in exactly the investigation it exists for.
        Assert.Throws<ArgumentException>(() => AuditEvent.Record(
            Occurred, AuditActions.SignIn, AuditOutcome.Success, AuditActorType.Employee));
    }

    [Theory]
    [InlineData(AuditActorType.Anonymous)]
    [InlineData(AuditActorType.System)]
    public void AnUnattributedActor_IsAccepted(AuditActorType actorType)
    {
        // Anonymous covers the sign-in attempt that fails before anyone is identified; System
        // covers background work. Both legitimately have no Employee.
        var recorded = AuditEvent.Record(
            Occurred, AuditActions.SignIn, AuditOutcome.Failure, actorType);

        Assert.Null(recorded.ActorEmployeeId);
    }

    [Fact]
    public void AnEventWithNoCompany_IsAccepted()
    {
        // A sign-in for an address matching no Employee has no tenant: the Company is the result
        // of the lookup, not an input to it. Refusing to record it would discard the attempts most
        // worth detecting.
        var recorded = AuditEvent.Record(
            Occurred, AuditActions.SignIn, AuditOutcome.Failure, AuditActorType.Anonymous);

        Assert.Null(recorded.CompanyId);
    }

    [Fact]
    public void TheStreamEntryIdentifier_IsUnsetUntilAStreamExists()
    {
        // The column and its unique index exist because DD-6 specifies them, but §3.3's durable
        // stream is not built and emission writes straight through — so nothing has a stream entry
        // to record. Asserted so the day it becomes non-null is a deliberate change.
        Assert.Null(Recorded().StreamEntryId);
    }

    // ---- Immutability (AU-1) -------------------------------------------------------------------

    [Fact]
    public void TheAggregate_ExposesNoWayToChangeARecordedEvent()
    {
        // The structural half of AU-1: "no update or delete path exists in code". A permission can
        // be misconfigured; a method that does not exist cannot be called.
        //
        // Every property is init-only, so this fails the moment somebody adds a setter — which is
        // the change that would quietly make the store editable.
        var settable = typeof(AuditEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { } setter
                               && setter.IsPublic
                               && !setter.ReturnParameter
                                   .GetRequiredCustomModifiers()
                                   .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(settable);
    }

    [Fact]
    public void TheAggregate_ExposesNoMutatingMethod()
    {
        // Corrections are compensating rows, never edits (§8.2). Any public instance method beyond
        // what every object has would be a way to change a recorded event.
        var methods = typeof(AuditEvent)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToList();

        Assert.Empty(methods);
    }

    [Fact]
    public void TheOnlyWayToCreateAnEvent_IsTheFactory()
    {
        // A public constructor would bypass the validation and the sanitization together.
        Assert.Empty(typeof(AuditEvent).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    // ---- Vocabulary ----------------------------------------------------------------------------

    [Fact]
    public void TheActionVocabulary_IsTheOneIdentityEmitsAgainst()
    {
        // The constants live in Shared because `identity` emits against them without referencing
        // this module (ADR-0002 R-5). If they were duplicated here the two would drift, and the
        // trail would carry both spellings of the same event.
        Assert.Equal(
            "MaintOrbit.Shared",
            typeof(AuditActions).Assembly.GetName().Name);

        Assert.Equal(
            "MaintOrbit.Shared",
            typeof(AuditTargets).Assembly.GetName().Name);
    }

    [Fact]
    public void EveryDocumentedAction_IsNamespacedAndLowercase()
    {
        // The vocabulary is exported to customers (AU-6) and searched by auditors (AU-5), so a
        // consistent shape matters more than any individual name. `category.verb`, lower case,
        // hyphenated — asserted so a fourteenth action cannot arrive in a different style.
        var actions = Constants(typeof(AuditActions));

        Assert.NotEmpty(actions);

        foreach (var action in actions)
        {
            Assert.Contains(".", action, StringComparison.Ordinal);
            Assert.Equal(action.ToLowerInvariant(), action);
            Assert.DoesNotContain(" ", action, StringComparison.Ordinal);
            Assert.DoesNotContain("_", action, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDocumentedTarget_IsLowercase()
    {
        var targets = Constants(typeof(AuditTargets));

        Assert.NotEmpty(targets);

        foreach (var target in targets)
        {
            Assert.Equal(target.ToLowerInvariant(), target);
            Assert.DoesNotContain(" ", target, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheVocabulary_HasNoDuplicateValues()
    {
        // Two constants with the same value would make two distinct events indistinguishable in
        // the trail, which is the one place that must be able to tell them apart.
        var actions = Constants(typeof(AuditActions));

        Assert.Equal(actions.Count, actions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryActionFits_TheStoredColumn()
    {
        // 128 characters. A name that did not fit would fail at insert, and emission is fail-open,
        // so the event would be lost rather than refused.
        Assert.All(Constants(typeof(AuditActions)), action => Assert.True(action.Length <= 128));
        Assert.All(Constants(typeof(AuditTargets)), target => Assert.True(target.Length <= 64));
    }

    private static AuditEvent Recorded() => AuditEvent.Record(
        Occurred, AuditActions.SignIn, AuditOutcome.Success, AuditActorType.System);

    private static IReadOnlyList<string> Constants(Type type) =>
        [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(field => (string)field.GetRawConstantValue()!)];
}
