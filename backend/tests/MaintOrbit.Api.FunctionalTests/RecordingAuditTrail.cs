using System.Collections.Concurrent;
using MaintOrbit.Application.Abstractions.Auditing;
using MaintOrbit.Shared.Auditing;

namespace MaintOrbit.Api.FunctionalTests;

/// <summary>
/// Captures audit events so a test can assert what was recorded.
/// </summary>
/// <remarks>
/// Substituted for <c>IAuditTrail</c> rather than for the sink, so the assertions are about what a
/// handler emitted rather than about how the placeholder sink formats it. The ambient actor and
/// correlation are the real trail's to fill; a test that needed them asserts through the sink
/// instead.
/// <para>
/// <b>It never throws.</b> The real trail is fail-open (SD-004), and a capture that threw would
/// make every test using it assert a stricter contract than production has.
/// </para>
/// </remarks>
internal sealed class RecordingAuditTrail : IAuditTrail
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    /// <summary>Everything recorded so far, in order.</summary>
    public IReadOnlyList<AuditEvent> Events => [.. _events];

    /// <summary>Every event for one action.</summary>
    public IReadOnlyList<AuditEvent> For(string action) =>
        [.. _events.Where(e => e.Action == action)];

    /// <summary>The single event for one action, or a failure naming what was recorded instead.</summary>
    public AuditEvent Single(string action)
    {
        var matches = For(action);

        Assert.True(
            matches.Count == 1,
            $"Expected exactly one '{action}' event; found {matches.Count}. " +
            $"Recorded: {string.Join(", ", _events.Select(e => e.Action))}");

        return matches[0];
    }

    public void Clear() => _events.Clear();

    public Task RecordAsync(
        string action,
        AuditOutcome outcome,
        string? targetType = null,
        string? targetId = null,
        IReadOnlyDictionary<string, string>? context = null,
        CancellationToken cancellationToken = default)
    {
        _events.Enqueue(new AuditEvent(
            DateTimeOffset.UtcNow, action, outcome, AuditActorType.Employee,
            TargetType: targetType, TargetId: targetId, Context: context));

        return Task.CompletedTask;
    }

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(auditEvent);

        return Task.CompletedTask;
    }
}
