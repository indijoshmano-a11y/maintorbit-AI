# ADR-0021 — Classify every hot-path dependency as fail-open or fail-closed

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0021 |
| **Title** | Classify every hot-path dependency as fail-open or fail-closed, expressed in the type system |
| **Status** | **Accepted** — one classification unresolved (decision D-3) |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Product |
| **Implements** | AD-012, FR-GW-017, FR-GW-018 |
| **Supersedes** | — |

---

## 1. Context

The Gateway depends on several subsystems during a request. Each can fail independently,
and the correct response differs fundamentally between them.

If **metering** fails, rejecting the request would turn a platform bookkeeping problem
into a customer outage — unacceptable for a component in a production request path.

If **authorization** fails, allowing the request would grant unauthorized access —
unacceptable for a governance product whose entire premise is enforcement.

Handling this per-call with scattered `try`/`catch` blocks makes the default behaviour an
accident of how each call site was written, and makes the system's actual failure
semantics undiscoverable.

## 2. Problem Statement

How should the Gateway behave when each dependency is unavailable, and how can that
behaviour be made explicit, uniform, and impossible to get wrong by omission?

## 3. Decision

**Every subsystem the hot path touches is classified as fail-open or fail-closed, and the
classification is expressed in the type system rather than in per-call error handling.**

| Fail **open** — request proceeds | Fail **closed** — request rejected |
| --- | --- |
| Usage metering | Authentication |
| Audit emission *(alerts as incident)* | Authorization |
| Analytics projection | Tenant context resolution |
| Notification | Budget enforcement |
| Telemetry | Governance policy evaluation |
| Decision record emission | Quota enforcement *(see §7)* |

**The organizing principle:** availability and bookkeeping concerns degrade open, so a
platform fault never becomes a customer outage. Security and financial controls degrade
closed, because an unenforced control is indistinguishable from no control.

**Expressing this in the type system**, rather than in convention, means the default for a
newly introduced dependency is a deliberate choice — a developer must state which category
it belongs to, and cannot silently inherit whichever behaviour the surrounding `catch`
block happened to have.

**Fail-open does not mean unnoticed.** Audit emission is fail-open so a platform fault
does not cause an outage, but FR-AUD-011 requires that a failure to record is treated as
an **incident**: recorded, alerted, and reconciled.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Fail closed on everything | Any dependency failure rejects the request | Maximally safe; a metering fault would cause a full outage. Unacceptable for a component in customers' production request paths |
| Fail open on everything | Any dependency failure allows the request | Maximally available; budget and governance become advisory, failing FR-COST-007 and FR-GOV-002's enforce mode. A governance product that stops governing under load is not a governance product |
| Per-call `try`/`catch` with local decisions | Handle each call site individually | **The default outcome without a decision.** Behaviour becomes an accident of authorship; the system's failure semantics cannot be stated, let alone tested |
| Runtime-configurable per dependency | Operators choose behaviour per subsystem | Attractive flexibility; makes failure semantics unpredictable across deployments and untestable in CI. **Except** for one case — see §7 |

## 5. Pros

- **Failure behaviour is stated, uniform, and testable** rather than emergent.
- **NFR-AVAIL-015 becomes satisfiable** — documented failure modes for every dependency,
  which is what the P-02 persona requires and most competitors do not provide.
- **A platform fault cannot become a customer outage** for the majority of dependencies.
- **Security and financial controls cannot be silently degraded** under load, which is
  exactly when the temptation to degrade them is strongest.
- **New dependencies force a decision** rather than inheriting an accidental default.

## 6. Cons

- **Fail-closed dependencies become hard availability dependencies.** Redis carries
  quota and budget counters, so Redis unavailability halts the Gateway — the architecture's
  principal availability exposure (ADR-0006 R-1).
- **The classification is a judgement**, and some subsystems are genuinely ambiguous.
- **Type-level expression adds structure** that is more ceremony than a `try`/`catch` for
  simple cases.
- **Fail-open paths need their own alerting discipline**, or degradation becomes invisible.

## 7. The unresolved classification — decision D-3

**Budget and quota enforcement are currently classified fail-closed, which means Redis
unavailability halts the Gateway entirely.**

This is defensible on principle: a budget that stops enforcing when infrastructure is
degraded is not a hard limit, and the P-05 persona treats a hard limit as a hard limit.

It is also a severe availability consequence. Per
[`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) §3.6,
Redis loss halts inference through this path even though the Gateway could otherwise
continue serving from cache.

**The options:**

| Option | Behaviour during Redis outage | Consequence |
| --- | --- | --- |
| **A — Keep fail-closed** | Gateway rejects all requests | Availability target must account for Redis availability; a Redis restart is an outage |
| **B — Fail open with a bounded allowance** | Requests proceed; spend accrues unmetered for a bounded window, reconciled afterwards | Preserves availability; a customer could exceed a hard budget during the window |
| **C — Configurable per Company** | Customer chooses | Honest — it is genuinely their risk trade-off — but makes failure semantics vary per tenant |

**This ADR does not decide it.** It is decision D-3 and requires a product judgement about
which failure a customer would rather have. Whichever is chosen must be **documented in
customer-facing failure-mode material** (NFR-AVAIL-015), because both options surprise
someone.

## 8. Consequences

- **Every subsystem must be classified during design**, and the classification covered by
  tests. An unclassified dependency is an incomplete design.
- **Both categories require failure-injection testing.** NFR-AVAIL-015 requires *observed*
  behaviour, not asserted behaviour — every fail-open and fail-closed path is a hypothesis
  until injected.
- **Fail-open paths must alert.** Silent degradation is worse than failure because it is
  discovered later, by which point data is missing.
- **Fail-closed dependencies define the availability floor.** The Gateway cannot be more
  available than the least available of its fail-closed dependencies, which is why
  ADR-0022's topology decision matters so much.
- **Load shedding must respect this classification** — usage and audit recording are never
  shed (`scalability-strategy.md` §3.8), even though they are fail-open. Fail-open covers
  *dependency failure*, not deliberate capacity management.

## 9. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Fail-closed Redis dependency halts the Gateway during routine maintenance | **Critical** | Medium | Replication with automatic failover; decision D-3 |
| R-2 | A fail-open path degrades silently and data is lost before anyone notices | High | Medium | Alerting on every fail-open activation; reconciliation detects divergence |
| R-3 | A new dependency ships unclassified, inheriting accidental behaviour | High | Medium | Type-level expression makes classification mandatory; review gate |
| R-4 | Classifications are asserted but never injection-tested | High | **High** | Failure-injection testing is a release requirement per NFR-AVAIL-015 |
| R-5 | Fail-open is misread as permission to shed usage under load | High | Medium | Explicitly distinguished in §8; `scalability-strategy.md` §3.8 states it separately |

## 10. Future Revisions

Revisit when:

- **Decision D-3 is made.** This ADR should be amended to record the outcome and its
  rationale.
- **Redis roles are separated** (ADR-0006 stage 2). Separating counters from cache changes
  the failure correlation — a cache instance failure would no longer imply a counter
  failure, materially reducing risk R-1.
- **The Gateway is extracted** (ADR-0002 §9). Remote calls to Governance and Usage would
  introduce new fail-classification decisions for network partitions.
- **Response-side governance is added.** Failure semantics for a control that acts on a
  partially-delivered response are genuinely different and need their own analysis.

## 11. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-012; §8 decision D-3 |
| [`../02-architecture/ai-gateway-architecture.md`](../02-architecture/ai-gateway-architecture.md) | §3.1 stage classification |
| [`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) | §3.6 failure impact analysis |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | §3.8 shedding, distinct from fail-open |
| [`ADR-0006-redis.md`](ADR-0006-redis.md) | The dependency this classification makes critical |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | Where classification applies |
| [`ADR-0011-usage-audit-ingestion.md`](ADR-0011-usage-audit-ingestion.md) | Emission is fail-open with alerting |
| [`ADR-0022-deployment-topology.md`](ADR-0022-deployment-topology.md) | Availability floor set by fail-closed dependencies |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-AVAIL-007/008/015 |
