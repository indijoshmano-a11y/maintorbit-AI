# ADR-0001 — Adopt Clean Architecture layering

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0001 |
| **Title** | Adopt Clean Architecture layering with inward-pointing dependencies |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-001 |
| **Supersedes** | — |

---

## 1. Context

MaintOrbit AI is an enterprise control plane expected to run for years, accumulate 230
functional requirements' worth of behaviour, and eventually have modules extracted into
separate services (NFR-MAINT-003). Its domain contains genuine invariants — budget
enforcement, cost calculation with a 2% accuracy tolerance (NFR-DATA-003), immutable
audit records (FR-AUD-003) — that must not be entangled with persistence or transport
concerns.

Phase 0 fixed a five-project repository structure: `Api`, `Application`, `Domain`,
`Infrastructure`, `Shared`. This ADR records the dependency discipline governing them.

## 2. Problem Statement

How should the backend be layered so that domain rules remain testable and stable while
persistence, transport, and provider integrations change independently — and so that the
layering survives years of delivery pressure?

## 3. Decision

Dependencies point inward. Specifically:

- `Domain` references `Shared` only. It contains no EF Core types, no HTTP types, no
  provider SDKs, and no framework service-location.
- `Application` references `Domain` and `Shared`. It declares **ports** — interfaces
  describing what it needs from the outside world.
- `Infrastructure` references `Application` and `Domain` and **implements** those ports.
  This inversion is the mechanism that keeps the inner layers free of infrastructure.
- `Api` is a composition root and transport surface only. It contains no business logic.
- `Shared` references nothing and contains no module-specific types.

The rule is enforced by executable architecture tests (ADR-0020, AT-1 and AT-2), not by
code review.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Traditional N-tier | Controller → Service → Repository → Database, with dependencies pointing outward toward the database | Domain logic ends up coupled to persistence; testing requires a database; the domain becomes an anaemic set of data holders |
| Vertical slice only, no layers | Each feature is a self-contained slice with its own data access | Excellent for small systems; at 230 requirements it produces duplicated invariant enforcement and no place for genuinely shared domain rules |
| Hexagonal / Ports and Adapters | Functionally near-identical to what was chosen | Not rejected — this decision *is* essentially hexagonal. "Clean Architecture" is the vocabulary the team and Phase 0 structure already use |
| No enforced layering | Convention only | Conventions erode. The extraction premise (ADR-0002) depends on discipline holding for years under pressure |

## 5. Pros

- Domain logic is testable without a database, a network, or a provider account.
- Persistence technology can change without touching business rules — material given
  that Analytics is expected to outgrow PostgreSQL (ADR-0004 §9).
- Provider SDK churn is confined to `Infrastructure` (ADR-0009).
- The dependency rule is mechanically checkable, so it does not depend on reviewer
  attention.
- Supports NFR-MAINT-003 extraction by keeping module logic free of transport concerns.

## 6. Cons

- More indirection than a direct data-access approach. A simple read passes through more
  types than it strictly needs.
- Port-and-adapter pairs are ceremony for genuinely trivial operations.
- The inversion (`Infrastructure` → `Application`) is counter-intuitive to engineers
  expecting dependencies to follow runtime call order, and is a recurring onboarding
  question.
- Cannot be applied uniformly: the Gateway hot path must bypass parts of this structure
  to meet its latency budget (ADR-0010), creating a documented exception.

## 7. Consequences

- Every external dependency must be expressed as a port in `Application` before it can be
  used, which is a design step, not a coding step.
- Query paths are permitted to bypass the domain and read projections directly (BD-002),
  because reconstructing aggregates to render a chart is waste. This is a deliberate
  relaxation, not a violation.
- Direct SQL is permitted for Analytics aggregation only (BD-009). Permitting it
  generally would erode the model.
- The Gateway hot path is an explicit, bounded exception (ADR-0010) and must be
  understood as such rather than treated as precedent.
- Architecture tests become release-gating infrastructure that must itself be maintained.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Layering degrades into ceremony — anaemic domain with logic in handlers | Medium | High | Domain modelling conventions (BD-003, BD-008); review gate on handlers containing invariant logic |
| R-2 | The hot-path exception is treated as precedent for further exceptions | Medium | Medium | The exception is named, bounded, and documented in ADR-0010; new exceptions require an ADR |
| R-3 | Architecture tests are weakened or suppressed under delivery pressure | High | High | Tests are build-gating; suppression requires architecture review |
| R-4 | Engineers unfamiliar with the inversion place infrastructure concerns in `Application` | Medium | High | AT-2 fails the build; documented in coding standards |

## 9. Future Revisions

Revisit if:

- **Module extraction begins in earnest.** Extracted services may warrant lighter
  layering internally, since their scope is smaller. This should be a deliberate
  decision per service, not a drift.
- **The ceremony cost is measured and found material.** If a significant share of
  engineering time is spent on port-and-adapter plumbing for trivial operations, a
  relaxation for simple CRUD modules (Notifications, for example) may be justified.
- **The Gateway is extracted.** A dedicated Gateway service with a single responsibility
  may not need five layers.

This ADR would not be superseded by those changes — it would be amended to record the
scope of the relaxation.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.3 layering; AD-001 |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §3.1 project structure; §8 architecture tests |
| [`ADR-0002-modular-monolith.md`](ADR-0002-modular-monolith.md) | Module decomposition within these layers |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | The documented exception to this structure |
| [`ADR-0020-observability.md`](ADR-0020-observability.md) | Architecture test enforcement |
| [`ADR-0023-persistence-ef-core.md`](ADR-0023-persistence-ef-core.md) | How ports are implemented in `Infrastructure` |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-MAINT-001 … 003 |
