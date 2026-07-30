# ADR-0002 — Build a modular monolith, not microservices

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0002 |
| **Title** | Build a modular monolith with enforced boundaries and a defined extraction path |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Leadership |
| **Implements** | AD-001, AD-014 |
| **Supersedes** | — |

---

## 1. Context

MaintOrbit AI comprises twelve functional modules spanning identity, tenancy, provider
management, inference routing, chat, governance, metering, analytics, billing, audit,
notification, and observability.

These modules have genuinely different runtime profiles. The Gateway is
latency-critical, high-throughput, and low-data-volume. Analytics is read-heavy over
hundreds of millions of records. Auditing is write-once and read-rarely. Notification is
event-driven and bursty. Under microservices orthodoxy these would be separate services
from day one.

Countervailing facts: the team is small, the product is pre-revenue, no scaling
bottleneck has been *measured*, and NFR-MAINT-003 requires that any module be
extractable later without changing others.

## 2. Problem Statement

Should MaintOrbit AI be built as separate services from the outset, or as a single
deployable unit with internal boundaries strong enough to permit later extraction?

## 3. Decision

Build a **modular monolith**: one deployable application containing twelve modules with
strictly enforced boundaries.

Module interaction is governed by six rules:

| Rule | Statement |
| --- | --- |
| R-1 | A module may reference another module's **published contracts** only — never its entities, repositories, or internal services |
| R-2 | A module may not query another module's data store, including by join |
| R-3 | Cross-module communication is by contract call (synchronous) or integration event (asynchronous) |
| R-4 | Integration events are versioned, serializable, and carry no domain object references |
| R-5 | Shared reference data is duplicated by projection, not joined |
| R-6 | A module owns its schema; no other module holds a foreign key into it |

The module dependency graph must remain **acyclic**. Extraction, when it happens,
requires only three changes: replace the in-process event relay with a broker, replace a
direct contract call with a remote client of the same shape, and move the module's schema
to its own database.

**Extraction is not a milestone.** It happens when a module's measured scaling or
availability profile demands it, and not before.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Microservices from day one | Twelve services with independent deployment | Distributed transactions, network debugging, twelve deployment pipelines, and operational overhead — paid immediately for scaling benefits that are speculative at zero customers. The team is not sized for it |
| Unstructured monolith | One application, no internal boundaries | Fastest initially; forecloses NFR-MAINT-003 permanently. Retrofitting boundaries into an entangled codebase is a rewrite |
| Service-per-bounded-context, coarser grained | Three or four services: control plane, ledger, platform services | Fewer moving parts than full microservices but still distributed. The boundaries would be guesses made before any load data exists |
| Modular monolith with project-per-module | Sixty projects for twelve modules across five layers | Compiler-enforced boundaries — genuinely stronger than the chosen approach. Rejected on build time, reference management, and the fact that Phase 0 fixed a five-project structure |

## 5. Pros

- **Transactional consistency within a module** without distributed coordination.
- **One deployment pipeline, one runtime to debug.** A stack trace crosses module
  boundaries intact.
- **Refactoring across boundaries is cheap** while boundaries are still being learned —
  and at zero customers, we do not yet know where they truly belong.
- **Extraction remains available** because the rules make module coupling contract-shaped
  from the start.
- Matches the team's size and the product's stage without foreclosing the future.

## 6. Cons

- **Modules cannot scale independently.** The Gateway's throughput profile forces
  provisioning that Analytics does not need, and vice versa.
- **No failure isolation.** A memory leak in Analytics affects the Gateway in the same
  process. This is partly mitigated by separating the API and Worker hosts (ADR-0014).
- **Boundaries depend entirely on tests**, not the compiler (see ADR-0001 §6). A
  suppressed test is a lost boundary.
- **Eventual consistency between modules is required anyway** (BD-004), so some
  distributed-systems complexity is paid without the distributed-systems benefits.
- Deployment couples all modules: a change to Notification redeploys the Gateway.

## 7. Consequences

- **Cross-module consistency is eventual, never distributed-transactional.** A budget
  threshold crossing may be detected shortly after the request that crossed it. This must
  be visible in the product — it is part of why FR-ANL-008 requires freshness disclosure.
- **Architecture tests become load-bearing infrastructure.** AT-3 (no cross-module
  internal references) is what makes this decision reversible.
- **Shared reference data is duplicated by projection.** A module needing another's data
  holds a projection maintained by events, not a join.
- **The API and Worker hosts are separated from day one** (ADR-0014) to protect the
  Gateway's latency budget from batch work — a partial recovery of the isolation
  microservices would have given.
- **Extraction readiness varies sharply by module.** Auditing, Analytics, Notifications,
  and Observability are near-ready; Identity and Tenancy are not extractable in practice
  because everything depends on them.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Boundaries erode under delivery pressure, foreclosing extraction | High | **High** | AT-3 is build-gating; violations fail the build; suppression requires architecture review |
| R-2 | A dependency cycle is introduced and the test is disabled rather than the cycle fixed | High | Medium | Cycle detection is a hard failure; architecture review required to suppress |
| R-3 | Extraction is attempted for fashion rather than measured need | Medium | Medium | Explicit anti-goal: extraction requires a measured scaling or availability driver |
| R-4 | Gateway extraction proves infeasible because Governance cannot be called remotely inside the latency budget | Medium | Medium | Policy evaluation is already cache-resident; co-deployment as a sidecar is the anticipated answer |
| R-5 | A single process failure affects all modules | Medium | Medium | API/Worker separation; horizontal API instances; the Gateway runs from cache and survives PostgreSQL loss |

## 9. Future Revisions

Revisit when **any** of the following is measured, not anticipated:

- **Gateway CPU saturation** at roughly 300–500 requests/second per host with no further
  vertical headroom, making independent Gateway scaling economically material.
- **Analytics query load** degrading Gateway or management performance despite read
  replicas.
- **Deployment coupling becoming a delivery constraint** — for example, Gateway changes
  being delayed by unrelated module release readiness.
- **A customer requirement for separate deployment** of a specific function.

The expected first extraction is the **Gateway**, because its profile diverges most.
Expected order thereafter: Analytics, then Usage. Identity and Tenancy should probably
never be extracted.

Extraction of a module does not supersede this ADR — it is the outcome this ADR plans
for. A superseding ADR would only be warranted if the modular monolith approach itself
were abandoned.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.4 module decomposition; AD-001, AD-014 |
| [`../02-architecture/component-diagram.md`](../02-architecture/component-diagram.md) | §3.3 dependency map; §3.7 extraction readiness per module |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §3.2 module anatomy; §8 architecture tests |
| [`ADR-0001-clean-architecture.md`](ADR-0001-clean-architecture.md) | Layering within which modules sit |
| [`ADR-0013-outbox-eventing.md`](ADR-0013-outbox-eventing.md) | The transport that makes extraction a swap |
| [`ADR-0004-postgresql.md`](ADR-0004-postgresql.md) | Schema-per-module supporting R-6 |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-MAINT-001 … 003 |
