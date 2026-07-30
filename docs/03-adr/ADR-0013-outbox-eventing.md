# ADR-0013 — Cross-module events use an in-process bus with a transactional outbox

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0013 |
| **Title** | Cross-module communication uses integration events with a transactional outbox |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-003, AD-014, BD-004 |
| **Supersedes** | — |

---

## 1. Context

ADR-0002 requires that modules communicate only through published contracts and events,
and that any module be extractable later by changing transport alone. That promise is
worth exactly as much as the eventing mechanism's reliability.

Several critical behaviours depend on events arriving: cost calculation follows usage
persistence; analytics projections follow cost; audit events follow configuration
changes; cache invalidation follows credential and policy changes — and a lost
invalidation event is a **security** defect, not a staleness annoyance (ADR-0010 R-2).

The classic failure is publishing after commit without atomicity: the state change
commits, the process crashes, the event is never published, and the two sides diverge
permanently with no error anywhere.

## 2. Problem Statement

How can cross-module events be published atomically with the state changes that cause
them, using a transport that can be swapped for a message broker at extraction time
without changing publishers or consumers?

## 3. Decision

**Integration events are written to a transactional outbox in the publishing module's
schema, inside the same transaction as the state change. A background relay dispatches
them.**

```
Command handler ─┬─▶ state change   ┐
                 └─▶ outbox record  ┘ one transaction, one commit
                                     │
                            background relay
                                     ▼
                          in-process dispatch (today)
                          message broker (after extraction)
```

| Property | Decision |
| --- | --- |
| Atomicity | Outbox write is in the handler's transaction — no separate commit |
| Dispatch | Behaviour position 7 in the ADR-0012 pipeline, **after** commit |
| Delivery | At-least-once; **consumers must be idempotent** |
| Event shape | Versioned, serializable, identifiers and primitives only — **no domain object references** |
| Ordering | Not guaranteed across aggregates; consumers must not depend on it |
| Transport | In-process today; broker after extraction — **relay changes, publishers and consumers do not** |

**Cross-module consistency is eventual, never distributed-transactional.** A handler
needing to affect another module commits its own work and publishes; the consuming module
reconciles.

**Cache invalidation is implemented as an event consumer** (CD-003), reusing this path
rather than adding a second propagation mechanism.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Direct synchronous calls between modules for everything | Simple, immediately consistent | Couples modules at runtime, creates cycles, and forecloses extraction. Some synchronous contract calls remain where the caller genuinely needs the result, but they are the minority |
| Publish after commit, no outbox | Simplest asynchronous approach | **A crash between commit and publish loses the event silently.** For cost, audit, and cache invalidation this is unacceptable — the divergence is permanent and produces no error |
| Distributed transactions across modules | Two-phase commit | Forecloses extraction permanently, and two-phase commit across a future service boundary is a known operational trap |
| Message broker from day one | Kafka, RabbitMQ, or NATS immediately | Correct eventual destination. Rejected now on operational surface (NFR-PORT-002 self-hosting, one more system to run) when there is only one process to deliver within |
| Database change-data-capture | Derive events from the write-ahead log | Removes the outbox write, but couples event shape to schema shape — the opposite of what ADR-0002 R-4 requires |

## 5. Pros

- **Atomicity.** An event exists if and only if the state change committed.
- **Extraction is a relay swap.** Publishers and consumers are unchanged; this is the
  mechanism that makes ADR-0002's promise credible rather than aspirational.
- **Events survive process restart** because they are durable in PostgreSQL before
  dispatch.
- **Modules stay decoupled at runtime** — a slow or failing consumer does not fail the
  publisher.
- **One propagation mechanism** serves cross-module notification and cache invalidation.

## 6. Cons

- **Write amplification.** Every event costs an extra row in the publisher's transaction.
- **Delivery latency measured in seconds**, not microseconds — which is why the hot path
  uses ADR-0011's stream instead.
- **Eventual consistency is now visible in the product.** A budget threshold crossing may
  be detected shortly after the request that crossed it. FR-ANL-008's freshness disclosure
  exists partly because of this.
- **At-least-once delivery makes idempotency mandatory** in every consumer, and a
  non-idempotent consumer corrupts state on redelivery.
- **The outbox becomes a hot table** at high throughput and will need partitioning.
- **No ordering guarantee** across aggregates, which is occasionally surprising.

## 7. Consequences

- **Every event consumer must be idempotent.** This is a design requirement and a review
  gate, not a recommendation. Hangfire retries (ADR-0014) compound the same requirement.
- **Events carry identifiers and primitives only.** An event referencing a domain object
  would couple modules through the event payload, defeating the purpose — verified by
  architecture test AT-7.
- **Events are versioned** so that a future extracted consumer can tolerate a publisher on
  a different version.
- **The relay runs elevated** (crossing Company boundaries), so each event handler must
  re-establish its own tenant context before data access (ADR-0005 §7).
- **Cache invalidation inherits event delivery latency**, which is why ADR-0010's
  60-second time-to-live ceiling exists as a hard backstop.
- **The outbox will need partitioning by module and time** at NFR-SCAL-002 throughput.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | A non-idempotent consumer corrupts state on redelivery | High | Medium | Idempotency is a review gate; deduplication by event identifier; reconciliation detects divergence |
| R-2 | Relay falls behind, delaying cache invalidation and cost calculation | Medium | Medium | Outbox depth alerting; 60 s TTL ceiling bounds the security consequence |
| R-3 | An event handler omits tenant context and silently processes nothing | Medium | High | Explicit context establishment required; ADR-0005's safe failure direction limits harm to confusion |
| R-4 | The outbox becomes a write hot spot | Medium | High | Partitioning by module and time; relay batching |
| R-5 | Consumers come to depend on ordering that is not guaranteed | Medium | Medium | Documented as unordered; ordering-dependent logic is a review finding |
| R-6 | An invalidation event storm during bulk role change overwhelms the relay | Medium | Medium | Batched invalidation; TTL ceiling bounds the consequence of delay |

## 9. Future Revisions

Revisit when:

- **The first module is extracted** (expected: Gateway). The relay gains a broker
  transport. This is the anticipated outcome and does not supersede this ADR — the outbox
  pattern and event contracts are unchanged.
- **Outbox write amplification becomes material** at high throughput. Partitioning first;
  change-data-capture reconsidered only if partitioning is insufficient.
- **Cross-region deployment (v2.1)** requires event replication across regions.
- **Ordering guarantees become genuinely necessary** for a use case. The answer is
  per-aggregate ordering in a broker, not global ordering.

The broker chosen at extraction should be evaluated together with ADR-0011's ingestion
replacement — they may well be the same system, which would reduce operational surface.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-003, AD-014 |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §3.6 unit of work; §3.7 relay job |
| [`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) | §3.3 event dependencies |
| [`ADR-0002-modular-monolith.md`](ADR-0002-modular-monolith.md) | The extraction promise this underwrites |
| [`ADR-0012-cqrs-dispatcher.md`](ADR-0012-cqrs-dispatcher.md) | Pipeline position of outbox dispatch |
| [`ADR-0014-hangfire.md`](ADR-0014-hangfire.md) | Relay execution; idempotency requirement |
| [`ADR-0011-usage-audit-ingestion.md`](ADR-0011-usage-audit-ingestion.md) | The hot-path alternative to this mechanism |
| [`ADR-0005-multi-tenant-strategy.md`](ADR-0005-multi-tenant-strategy.md) | Elevated relay and context re-establishment |
