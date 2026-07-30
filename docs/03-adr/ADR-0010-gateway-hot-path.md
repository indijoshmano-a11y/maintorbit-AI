# ADR-0010 — The Gateway hot path bypasses the dispatcher and reads only from cache

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0010 |
| **Title** | The Gateway hot path bypasses the dispatcher pipeline and performs no synchronous relational access |
| **Status** | **Accepted** — latency budget requires prototype validation |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-005, GD-001, GD-002, CD-001 |
| **Supersedes** | — |

---

## 1. Context

The Gateway sits in the request path of customers' production systems. NFR-PERF-001
allows **15 ms median platform overhead** — measured as end-to-end duration minus time
awaiting the provider — with 50 ms p95 and 100 ms p99.

Within that budget the platform must authenticate a Platform API Key, resolve tenant
context, authorize, check quota, check budget, evaluate governance policies, select a
route, and emit three record types.

A single synchronous PostgreSQL query — connection acquisition, round trip,
materialization — consumes a substantial fraction of 15 ms on its own. The hot path needs
six or more distinct pieces of state.

The P-03 persona will compare the Gateway directly against calling a provider SDK and
lists perceptible added latency as an abandonment trigger. Coverage — the platform's
central value measure — depends on developers choosing the governed path.

## 2. Problem Statement

How can the Gateway enforce authentication, authorization, quota, budget, and governance
within 15 ms median, when the standard request pipeline and relational access cannot fit
that budget?

## 3. Decision

**The Gateway hot path is an explicit, bounded exception to the standard architecture.**

Two decisions together:

**(a) It bypasses the dispatcher pipeline.** The behaviour chain — correlation, tenant
context, authorization, validation, transaction, outbox, audit, telemetry — provides
guarantees the Gateway still needs, but its transaction and validation behaviours alone
would consume a meaningful share of the budget. The hot path implements equivalent
guarantees through purpose-built components. **This is the only permitted exception to
the dispatcher rule.**

**(b) It performs no synchronous relational read or write.** All state is served from a
two-tier cache — in-process memory backed by Redis. All writes go to durable Redis streams
(ADR-0011).

**Budget allocation per stage** — exceeding an allocation is a defect, not a tuning
opportunity:

| Stage | p50 | p95 | Source |
| --- | --- | --- | --- |
| Authenticate | 2 ms | 6 ms | In-process cache + tombstone check |
| Resolve tenant | 1 ms | 3 ms | In-process cache |
| Authorize | 1 ms | 2 ms | In-process cache |
| Quota check | 1 ms | 2 ms | Redis counter |
| Budget check | 1 ms | 3 ms | Redis counter |
| Governance | 4 ms | 20 ms | Compiled policy, in-process |
| Route selection | 1 ms | 3 ms | In-process cache |
| Normalization | 2 ms | 6 ms | In-memory |
| Emission | 2 ms | 5 ms | Redis stream append |
| **Total** | **15 ms** | **50 ms** | NFR-PERF-001/002 |

**Cache time-to-live is capped at 60 seconds** for authorization-relevant state — a
security requirement, not a performance choice, guaranteeing FR-PERM-005 and FR-AUTH-010
even if an invalidation event is lost.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Use the standard pipeline everywhere | Architectural uniformity | Cannot fit the budget. Transaction and validation behaviours alone consume a meaningful share of 15 ms |
| Relational reads with aggressive query tuning | Keep PostgreSQL in the path, optimize hard | Connection acquisition plus round trip is irreducible; six pieces of state means six opportunities to breach the budget |
| Relax the latency budget | Accept 50–100 ms median overhead | **Rejected on product grounds.** The P-03 persona compares against direct SDK calls; perceptible overhead is an abandonment trigger and directly attacks the coverage goal |
| Move enforcement out of the request path entirely | Meter and enforce asynchronously | Budget and governance would become detection rather than enforcement, failing FR-COST-007 and FR-GOV-002's enforce mode |
| Separate Gateway service from day one | Extract immediately for independent optimization | Adds distributed complexity before any measurement justifies it; contradicts ADR-0002. Remains the expected first extraction |

## 5. Pros

- **The budget becomes achievable** rather than aspirational.
- **The Gateway survives PostgreSQL unavailability.** Because the hot path runs entirely
  from cache, inference continues while the database is down, with usage buffering in the
  stream. This is a genuine and somewhat surprising resilience property worth preserving
  deliberately.
- **Enforcement stays synchronous**, so budgets and policies actually block rather than
  merely report.
- **Horizontal scaling is unconstrained** by database connection capacity for inference
  traffic.

## 6. Cons

- **A second code path with its own correctness obligations.** Every guarantee the
  pipeline provides must be independently implemented and independently tested.
- **Cache correctness becomes a security property, not a performance one.** A stale entry
  can leave a revoked credential or role effective — the most serious consequence of this
  design.
- **Redis becomes a hard Gateway dependency** through both cache and counters
  (ADR-0006 R-1).
- **Every cached item needs an invalidation path**, and a missing one is a security defect
  rather than a staleness annoyance.
- **The 60-second ceiling caps how fresh authorization can be**, so a revoked role is
  effective for up to a minute absent the tombstone mechanism.
- The budget leaves **no slack**. Any new hot-path capability requires taking time from
  an existing stage.

## 7. Consequences

- **Revocation tombstones are mandatory** (ADR-0007). Time-to-live alone leaves a
  60-second window in which a revoked key still works, which fails the spirit of
  FR-AUTH-018 and the P-07 persona's explicit test. The tombstone check costs one Redis
  round trip per request and is charged to the authenticate stage.
- **Governance policies must be compiled on change, not interpreted per request.**
  Interpretation cannot fit a 20 ms allocation at target throughput.
- **Monitor mode must cost the same as enforce mode**, or customers will disable it —
  defeating the observe-before-enforce principle that makes governance adoptable.
- **Hot-path and pipeline equivalents must be tested together.** A shared test suite must
  assert that both paths enforce the same authorization and audit outcomes, or they will
  drift.
- **Key last-used tracking cannot be a per-request write** — it is derived or coarse.
- **A latency regression gate is required.** NFR-PERF-018 mandates continuously measured
  and published overhead; a benchmark failing the build on regression is the only way this
  survives ongoing change.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | The 15 ms budget proves unachievable with all stages enabled | **Critical** | Medium | **Prototype the complete hot path before further scope is committed.** Targets are hypotheses until measured |
| R-2 | A stale cache entry leaves a revoked credential or role effective | **Critical** | Medium | Tombstones; 60 s ceiling; invalidation event delivery monitored |
| R-3 | Hot-path components drift from pipeline equivalents, losing a guarantee | High | Medium | Shared test suite asserting identical authorization and audit outcomes |
| R-4 | Governance exceeds its 20 ms allocation as policy count grows | High | **High** | Compilation; per-policy cost measurement; a per-Company policy-count limit may become necessary |
| R-5 | The exception is treated as precedent for further pipeline bypasses | Medium | Medium | Named and bounded here; any new exception requires its own ADR |
| R-6 | Garbage collection pauses breach p99 despite median compliance | High | Medium | Allocation discipline; profiling; server GC tuning (ADR-0003 R-1) |

## 9. Future Revisions

Revisit if:

- **The prototype fails risk R-1.** The response ladder, in order: profile and reduce
  allocations; move governance evaluation to a co-located sidecar; extract the Gateway
  into a dedicated service (ADR-0002 §9); and only then reconsider the budget itself. The
  budget is a product requirement, so relaxing it is a product decision, not an
  engineering one.
- **Governance grows beyond its allocation** (R-4). Likely responses are per-Company
  policy limits, or splitting evaluation into a fast pre-filter and a slower full
  evaluation applied selectively.
- **The Gateway is extracted.** A dedicated service could keep more state in process and
  might not need the same cache architecture — though it would still need shared counters
  and shared circuit breaker state.
- **Response-side governance is added.** Evaluating completions under streaming has
  fundamentally different characteristics and will need its own budget analysis.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/ai-gateway-architecture.md`](../02-architecture/ai-gateway-architecture.md) | §3.1 stages; §3.2 budget; §3.3 cache |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.5 hot path vs management path; AD-005 |
| [`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) | §3.2 two request paths; §3.6 failure impact |
| [`ADR-0006-redis.md`](ADR-0006-redis.md) | Cache and counter roles |
| [`ADR-0007-authentication-strategy.md`](ADR-0007-authentication-strategy.md) | Tombstone mechanism this requires |
| [`ADR-0011-usage-audit-ingestion.md`](ADR-0011-usage-audit-ingestion.md) | The write side of the same constraint |
| [`ADR-0012-cqrs-dispatcher.md`](ADR-0012-cqrs-dispatcher.md) | The pipeline this bypasses |
| [`ADR-0021-fail-open-fail-closed.md`](ADR-0021-fail-open-fail-closed.md) | Failure classification within these stages |
| [`../01-product/user-personas.md`](../01-product/user-personas.md) | P-03 abandonment triggers |
