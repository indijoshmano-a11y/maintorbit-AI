# ADR-0011 — Ingest usage and audit records via durable stream, persist in batches

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0011 |
| **Title** | Ingest usage and audit records via a durable Redis stream, persisted in batches |
| **Status** | **Proposed** — durability gap must be resolved (decision D-2) |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Product |
| **Implements** | AD-006 |
| **Supersedes** | — |

> **This ADR documents a known gap between the design and a stated requirement.** It is
> recorded as Proposed rather than Accepted because §7 identifies a residual durability
> exposure that does not literally satisfy NFR-DATA-001. The gap must be closed or the
> requirement amended — concealing it would be the worse outcome.

---

## 1. Context

Three requirements collide directly:

| Requirement | Statement |
| --- | --- |
| **NFR-DATA-001 / 002** | **Zero** Usage Records or Audit Events lost |
| **NFR-DATA-007** | Neither may be sampled **under any load condition** |
| **NFR-PERF-001** | 15 ms median platform overhead, which forbids a synchronous relational write |

The completeness requirements are not incidental. Cost attribution that under-reports
breaches NFR-DATA-003's 2% tolerance and destroys the P-05 persona's trust. An audit
trail with gaps is worse than none — it creates false confidence, and the P-06 persona
treats sampled audit data as disqualifying. These are load-bearing product commitments,
recorded in `mission.md` §4.5 as an anti-goal: *"We will not sample the audit trail."*

## 2. Problem Statement

How can every request produce a durable, unsampled Usage Record and Audit Event when a
synchronous database write cannot fit the latency budget?

## 3. Decision

**The Gateway appends records to Redis Streams, acknowledged before the response returns.
A Worker consumer group batches them into PostgreSQL.**

```
Gateway ──append, sub-ms──▶ Redis Stream (AOF persistence)
                                  │
                            consumer group
                                  ▼
                        Worker batch writer ──batched insert──▶ PostgreSQL
                                  │
                            acknowledge
```

| Property | Decision |
| --- | --- |
| Append latency | Sub-millisecond; charged to the 2 ms emission allocation |
| Durability at append | Redis append-only file, per-second sync |
| Consumption | Consumer group with pending-entry tracking |
| Deduplication | By stream entry identifier — **mandatory**, since redelivery is normal |
| Failure to append | **Fail open** — the request still succeeds; the failure is alerted as an incident |
| Reconciliation | Scheduled job comparing stream offsets to persisted counts, alerting on divergence |

**All three record types are emitted for failed requests too.** A request rejected at
authentication still produces an audit event; a request rejected at budget check still
produces a usage record marked rejected. A ledger of successes only cannot support the
investigations it exists to support.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Synchronous write to PostgreSQL | Strongest durability | Consumes most or all of the 15 ms budget. Also makes PostgreSQL availability a hard Gateway dependency, losing the resilience property in ADR-0010 §5 |
| In-process buffer, periodic flush | Simplest asynchronous approach | **Loses everything buffered on process crash or restart.** Directly violates NFR-DATA-001. Rolling deployment would routinely lose records |
| Write-ahead log to local disk | Durable, no network hop | Ties records to a specific host; a lost host loses its unflushed records; complicates horizontal scaling and container replacement |
| **Kafka or NATS JetStream** | Purpose-built durable log with replication and stronger guarantees | **The technically strongest option.** Rejected *for MVP* on operational surface: an additional system to run, secure, back up, and self-host (NFR-PORT-002), for a small team. **This is the expected replacement — see §9** |
| Fire-and-forget to a collector | Lowest latency | No durability guarantee at all |

## 5. Pros

- **Reconciles the latency budget with the no-sampling requirement.** The record is
  durable before acknowledgement, and persistence cost is amortized across a batch.
- **Consumer-group pending-entry tracking** means a consumer crash cannot lose
  acknowledged-but-unwritten records — they remain pending and are redelivered.
- **Batched writes make PostgreSQL ingestion viable** at target throughput; row-by-row
  insertion would not be.
- **Buffering absorbs PostgreSQL unavailability.** Combined with ADR-0010, the Gateway
  continues serving inference while the database is down, with records accumulating.
- **No additional infrastructure** — Redis is already required for cache, counters, and
  backplane (ADR-0006).

## 6. Cons

- **Not literally zero-loss.** See §7.
- **Freshness lag.** Usage appears in analytics within 60 seconds (NFR-PERF-013), cost
  within 5 minutes (NFR-PERF-014). Users see slightly stale data, which is why FR-ANL-008
  requires freshness disclosure.
- **Redis memory becomes a correctness concern.** An evicted stream entry is a
  permanently lost record.
- **Deduplication is mandatory and easy to get subtly wrong.** A defect produces duplicate
  ledger records — corrupting exactly the data NFR-DATA-009 requires to be reproducible.
- **Backlog is invisible to users.** If the writer falls behind, data silently ages rather
  than failing loudly.

## 7. The durability gap — stated explicitly

**Redis append-only persistence with per-second fsync has a bounded loss window if the
Redis primary fails uncleanly. The residual exposure is approximately one second of
ingestion.**

This does **not** literally satisfy NFR-DATA-001's "zero" and must not be described as
if it does.

**Mitigations reduce but do not eliminate it:**

| Mitigation | Effect |
| --- | --- |
| Append-only file, per-second sync | Bounds the window to ~1 second |
| Replica with automatic failover | Most primary failures do not lose the window |
| Consumer-group pending entries | Consumer crashes lose nothing |
| Reconciliation job | Detects divergence; satisfies NFR-DATA-008 |
| No eviction on the streams instance | Prevents the far larger memory-pressure loss mode |

**Two honest resolutions exist, and decision D-2 must choose one:**

1. **Amend NFR-DATA-001** to state the bound explicitly — for example, "no loss except a
   bounded window under uncontrolled loss of the ingestion buffer primary, with
   reconciliation alerting on any divergence." This is defensible and, importantly,
   *truthful* to the P-06 persona.
2. **Fund a higher-durability intake** — a replicated log with synchronous acknowledgement
   — accepting the operational cost.

**What must not happen** is shipping option 1's behaviour while claiming option 2's
guarantee. `mission.md` §6 requires honesty about limitations, and the persona most
affected detects overstatement reliably and treats it as disqualifying.

## 8. Consequences

- **Deduplication by stream entry identifier is mandatory**, not optional. Consumer-group
  redelivery after a crash is normal operation, and without deduplication it produces
  duplicate ledger records — corrupting exactly the data NFR-DATA-009 requires to be
  reproducible.
- **The streams instance must have no eviction policy** and should be separated from the
  cache instance before production traffic (ADR-0006 stage 1). An evicted stream entry is a
  permanently lost record, and the loss produces no error and no alert.
- **A reconciliation job is required, not optional.** Comparing stream offsets against
  persisted counts on a schedule is what turns the zero-loss claim from an assumption into
  a monitored property, satisfying NFR-DATA-008.
- **Freshness must be exposed to users** (FR-ANL-008). Usage appears within 60 seconds and
  cost within 5 minutes, and a backlog silently ages data rather than failing loudly —
  so the lag must be visible rather than inferred.
- **Under capacity pressure, inference is shed before records are dropped**
  (`scalability-strategy.md` §3.8). This inverts the usual instinct and is a direct
  consequence of NFR-DATA-007.
- **Mid-stream client disconnect must still record usage** for tokens already consumed.
  The provider bills for them; discarding the record silently under-reports cost.
- **The batch writer runs in the Worker host** (ADR-0014) on a dedicated queue with its own
  allocation, protected from every other job class.
- **Whichever resolution decision D-2 selects must be documented honestly** in
  customer-facing material. This consequence falls on product and marketing, not only on
  engineering.

## 9. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Shared Redis instance evicts stream entries under memory pressure, silently losing ledger data | **Critical** | Medium | Dedicated streams instance with no eviction, before production traffic (ADR-0006 stage 1); memory alerting |
| R-2 | Deduplication defect produces duplicate ledger records | **Critical** | Medium | Deduplication by stream entry identifier; reconciliation compares counts; explicit redelivery test |
| R-3 | The ~1 s loss window is breached in a real failure and was never disclosed | High | Low | Decision D-2; honest documentation either way |
| R-4 | Batch writer cannot keep pace, growing the stream unboundedly | High | Medium | Stream depth alerting; scalable writer allocation; **shed inference before dropping records** |
| R-5 | Mid-stream client disconnect loses usage for consumed tokens, under-reporting cost | High | Medium | Stream drain or partial usage capture; reconciliation detects systematic divergence |
| R-6 | Backlog silently ages data without visible failure | Medium | Medium | Freshness exposed to users per FR-ANL-008; stream depth is an alerting condition |

## 10. Future Revisions

**A replicated durable log is the expected replacement for this role.** Revisit when any
of:

- **Decision D-2 selects option 2** — funding higher durability;
- **Ingestion throughput approaches Redis Streams' practical limits**;
- **Multi-region deployment (v2.1)** requires cross-region ingestion durability;
- **A regulated-enterprise contract** requires a stronger stated guarantee than option 1
  provides. This is the most likely trigger, and it will arrive with segment 3.2.

The replacement would take the ingestion role only. Redis would continue serving cache,
counters, and backplane — the roles have different requirements and should be allowed to
diverge in technology.

Note that agentic workloads will also require a **parent trace identifier** on Usage
Records. That is decision D-8 and is independent of this ADR, but it must be settled
before schema design because retrofitting it leaves every historical record without one.

## 11. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-006; §8 decision D-2 |
| [`../02-architecture/ai-gateway-architecture.md`](../02-architecture/ai-gateway-architecture.md) | §3.10 emission |
| [`../02-architecture/request-flow.md`](../02-architecture/request-flow.md) | F-9 persistence sequence |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | §3.8 never shed usage or audit |
| [`ADR-0006-redis.md`](ADR-0006-redis.md) | Stream role and eviction policy |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | The latency constraint driving this |
| [`ADR-0004-postgresql.md`](ADR-0004-postgresql.md) | Batched write target |
| [`ADR-0021-fail-open-fail-closed.md`](ADR-0021-fail-open-fail-closed.md) | Emission is fail-open with alerting |
| [`../01-product/mission.md`](../01-product/mission.md) | §4.5 no sampling; §6 honesty about limitations |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-DATA-001/002/007/008 |
