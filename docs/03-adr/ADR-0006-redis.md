# ADR-0006 — Use Redis for cache, counters, ingestion streams, and backplane

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0006 |
| **Title** | Use Redis for four distinct roles, separated by instance as scale requires |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-009 |
| **Supersedes** | — |

---

## 1. Context

Four architectural needs arise independently and all point to the same technology:

1. **Hot-path cache.** ADR-0010 forbids synchronous relational reads in the Gateway. Six
   or more pieces of state must be served in under 15 ms total (NFR-PERF-001).
2. **Atomic counters.** Quota and budget enforcement (FR-GW-012/013) need
   increment-and-test in ≤ 5 ms (NFR-PERF-008) across multiple API host instances.
3. **Durable ingestion buffer.** ADR-0011 needs a durable append that returns
   sub-millisecond, consumed by a batch writer.
4. **Real-time backplane.** SignalR across multiple instances (ADR-0015) requires shared
   message distribution.

NFR-PORT-002 requires everything to be self-hostable.

## 2. Problem Statement

Should these four needs be met by one technology or several, and what durability and
eviction guarantees does each require?

## 3. Decision

Use **Redis** for all four roles, with a **staged separation by instance** driven first
by correctness and only later by capacity.

| Role | Durability | Eviction | Failure consequence |
| --- | --- | --- | --- |
| **Cache** | None needed | **Permitted** | Latency spike, then recovery |
| **Counters** | Short-term | **Forbidden** | Quota and budget fail closed — Gateway halts |
| **Streams** | **Required — AOF, per-second sync** | **Forbidden** | **Permanent ledger loss** |
| **Backplane** | None needed | Permitted | Real-time updates degrade |

**Separation sequence:**

| Stage | Configuration | Trigger |
| --- | --- | --- |
| 0 | Single instance | Development and evaluation only |
| **1** | **Streams on a dedicated instance**; cache, counters, backplane share | **Before production traffic** — correctness, not capacity |
| 2 | All four separated | ~10,000 aggregate requests/second |
| 3 | Clustered, sharded by role and key space | Beyond single-node capacity |

**Stage 1 is mandatory before production traffic.** It is not a scaling step.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Four different technologies | Purpose-built store per role: in-memory cache, dedicated counter store, message broker, pub/sub | Four systems to operate, secure, back up, and self-host at a stage with a small team. Redis serves all four competently |
| In-process cache only, no Redis | Per-host memory | Cannot share counters or circuit breaker state across instances. Each host would rediscover a failing provider independently, multiplying customer-visible failures |
| Message broker for ingestion | Kafka, RabbitMQ, or NATS instead of Redis Streams | **The strongest alternative for role 3.** Kafka in particular offers stronger durability than AOF-per-second. Rejected on operational surface and self-hosting burden; revisit per §9 |
| PostgreSQL for counters | Reuse the existing store | A relational round-trip cannot meet NFR-PERF-008's 5 ms budget, and it would put a write in the hot path, violating ADR-0010 |
| Managed cloud cache | Reduced operations | Violates NFR-PORT-002 for the product; acceptable as an operational choice for our own hosting since the product depends on Redis, not a vendor's offering |

## 5. Pros

- **One technology, one operational surface** — one thing to secure, monitor, back up,
  and teach.
- **Sub-millisecond reads** make the hot-path budget achievable (ADR-0010).
- **Atomic operations** give correct quota and budget enforcement across instances
  without coordination.
- **Streams with consumer groups** provide durable append plus pending-entry tracking, so
  a consumer crash cannot lose acknowledged-but-unwritten records.
- **Shared circuit breaker state** prevents each host relearning provider failure
  independently.
- Fully self-hostable.

## 6. Cons

- **Redis becomes a hard dependency of the Gateway through two independent paths.**
  Cache loss and counter loss each halt inference — counters because budget checks fail
  closed. This is the architecture's single most consequential availability exposure.
- **AOF with per-second sync is not zero-loss.** A bounded loss window exists, which does
  not literally satisfy NFR-DATA-001. ADR-0011 addresses this directly.
- **Consolidating four roles concentrates risk.** A memory or CPU problem caused by one
  role affects all four until separated.
- **The eviction policy conflict is subtle and dangerous.** Cache entries *may* be
  evicted; stream entries *must not* be. One instance cannot express both.
- Weaker durability guarantees than a purpose-built log for the ingestion role.

## 7. Consequences

- **Stage 1 separation before production traffic is a hard requirement.** If cache and
  streams share an instance with a single eviction policy, memory pressure silently
  destroys Usage Records and Audit Events — breaching NFR-DATA-001 and -002 in a way that
  produces no error and no alert.
- **Replication with automatic failover is required from topology T1** (ADR-0022).
  Single-instance Redis makes routine maintenance a full Gateway outage.
- **Every cached item needs a defined invalidation path** driven by the integration event
  that changes its source of truth, plus a 60-second time-to-live ceiling as a backstop
  (ADR-0010).
- **Redis memory is a monitored capacity dimension**, with the cache hit ratio monitored
  alongside memory — memory alone does not reveal a cache thrashing.
- **Gateway behaviour during Redis unavailability is an unresolved product decision**
  (D-3): does budget enforcement fail open, or does the Gateway stop? This ADR does not
  decide it, and it materially affects the availability target.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Redis outage halts the Gateway through cache and counters simultaneously | **Critical** | Medium | Replication with automatic failover; decision D-3 on degraded operation |
| R-2 | Shared instance evicts stream entries, silently losing ledger data | **Critical** | Medium | Stage 1 separation before production; eviction policy verified in deployment tests; memory alerting |
| R-3 | AOF loss window breaches the literal reading of NFR-DATA-001 | High | Low | ADR-0011 §7 discloses the bound; reconciliation job detects divergence |
| R-4 | Cache working set outgrows memory as tenant count grows | Medium | Medium | Cache is evictable by design; monitor hit ratio, not only memory |
| R-5 | Failover duration exceeds Gateway tolerance, producing a visible outage | High | Medium | Automatic failover; client reconnection with retry; measured in failure-injection testing |

## 9. Future Revisions

Revisit the **ingestion role specifically** if:

- The AOF loss window is judged unacceptable after decision D-2, **or**
- Ingestion throughput approaches Redis Streams' practical limits, **or**
- Multi-region deployment (v2.1) requires cross-region ingestion durability.

The expected replacement for that role alone is a durable log — Kafka or NATS JetStream
— retained for ingestion while Redis continues to serve cache, counters, and backplane.
**Replacing Redis wholesale is not anticipated**; the roles have different requirements
and should be allowed to diverge in technology as they already diverge in configuration.

Revisit the **counter role** if decision D-3 concludes that budget enforcement may fail
open during a Redis outage — that would materially reduce risk R-1's severity and change
the availability calculus in ADR-0022.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-009; §6 risk R-2; §8 decision D-3 |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | §3.4 separation sequence |
| [`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) | §3.6 failure impact analysis |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | §3.6 eviction policy and replication |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | Cache role |
| [`ADR-0011-usage-audit-ingestion.md`](ADR-0011-usage-audit-ingestion.md) | Stream role and its durability gap |
| [`ADR-0015-signalr.md`](ADR-0015-signalr.md) | Backplane role |
| [`ADR-0022-deployment-topology.md`](ADR-0022-deployment-topology.md) | Replication requirement |
