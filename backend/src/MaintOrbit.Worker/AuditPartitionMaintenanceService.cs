using MaintOrbit.Application.Abstractions.Maintenance;
using MaintOrbit.Application.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MaintOrbit.Worker;

/// <summary>
/// Runs audit partition maintenance on a schedule.
/// </summary>
/// <remarks>
/// <b>A <see cref="BackgroundService"/> with a timer, not a job framework.</b> ADR-0014 chose
/// Hangfire for background work and that decision stands — but it is the right tool for the nine
/// job classes in its table, with queues, retries, and a dashboard. This Worker has one job, whose
/// retry is "run again tomorrow" and whose idempotency is structural. Adding a framework, its
/// PostgreSQL schema, and a package whose licence obligations are still open (TD-3) to schedule a
/// single timer would be cost with no matching benefit, and TD-3 is exactly the kind of open
/// decision CLAUDE.md §6 rule 10 says not to build on.
/// <para>
/// When the second and third job classes arrive, this is the moment to revisit ADR-0014 properly.
/// The maintenance itself is behind <see cref="IAuditPartitionMaintenance"/>, so moving it onto a
/// scheduler later changes this file and nothing else.
/// </para>
/// </remarks>
internal sealed partial class AuditPartitionMaintenanceService(
    IAuditPartitionMaintenance maintenance,
    IOptions<AuditPartitionOptions> options,
    ILogger<AuditPartitionMaintenanceService> logger,
    TimeProvider timeProvider)
    : BackgroundService
{
    /// <summary>
    /// The most recent cycle's outcome, for readiness reporting.
    /// </summary>
    /// <remarks>
    /// A background process has no request to fail, so "unhealthy" has to mean something a
    /// supervisor can observe. This is read by <see cref="WorkerHealth"/>; the value matters
    /// because a missing partition loses audit events silently — fail-open emission means nothing
    /// else reports it.
    /// </remarks>
    internal volatile bool LastCycleSucceeded = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.IntervalMinutes);

        Started(logger, options.Value.IntervalMinutes, options.Value.FutureMonths);

        // Runs immediately on start rather than waiting out the first interval. A deployment whose
        // partitions had already lapsed would otherwise stay broken for a day after the fix shipped.
        using var timer = new PeriodicTimer(interval, timeProvider);

        do
        {
            await RunCycleAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));

        Stopped(logger);
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await maintenance.RunAsync(stoppingToken).ConfigureAwait(false);

            LastCycleSucceeded = result.Succeeded;

            if (!result.Succeeded)
            {
                CycleReportedFailure(logger, result.Failure ?? "unknown");
                return;
            }

            if (result.LockAcquired)
            {
                CycleCompleted(
                    logger,
                    result.PartitionsCreated.Count,
                    result.PartitionsDropped.Count,
                    result.RetentionEligible.Count,
                    result.Unexpected.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a fault. Rethrowing would log a cancellation as an error on every
            // ordinary container stop.
            throw;
        }
        catch (Exception error)
        {
            // The loop must survive. The maintenance already converts ordinary failures into a
            // result rather than an exception, so reaching here means something unanticipated —
            // and a Worker that exited on it would stop creating partitions altogether, which is
            // the failure this whole milestone exists to prevent.
            LastCycleSucceeded = false;
            CycleThrew(logger, error);
        }
    }

    /// <summary>Waits for the next tick, treating shutdown as a clean end rather than a fault.</summary>
    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(
        EventId = 1710,
        Level = LogLevel.Information,
        Message = "Audit partition maintenance started. Interval {IntervalMinutes} minutes, " +
                  "horizon {FutureMonths} months.")]
    private static partial void Started(ILogger logger, int intervalMinutes, int futureMonths);

    [LoggerMessage(
        EventId = 1711,
        Level = LogLevel.Information,
        Message = "Audit partition maintenance cycle complete. Created {Created}, dropped " +
                  "{Dropped}, {Eligible} past retention, {Unexpected} unexpected.")]
    private static partial void CycleCompleted(
        ILogger logger, int created, int dropped, int eligible, int unexpected);

    [LoggerMessage(
        EventId = 1712,
        Level = LogLevel.Error,
        Message = "Audit partition maintenance cycle failed: {Reason}. Audit emission is " +
                  "fail-open, so a missing partition loses events rather than failing a request.")]
    private static partial void CycleReportedFailure(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1713,
        Level = LogLevel.Error,
        Message = "Audit partition maintenance threw unexpectedly. The loop continues.")]
    private static partial void CycleThrew(ILogger logger, Exception error);

    [LoggerMessage(
        EventId = 1714,
        Level = LogLevel.Information,
        Message = "Audit partition maintenance stopped.")]
    private static partial void Stopped(ILogger logger);
}
