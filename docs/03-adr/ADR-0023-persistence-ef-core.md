# ADR-0023 — Use EF Core with interceptors, and direct SQL for analytics only

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0023 |
| **Title** | Use Entity Framework Core for command-side persistence, with direct SQL permitted for analytics only |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | BD-002, BD-005, BD-008, BD-009 |
| **Supersedes** | — |

---

## 1. Context

Persistence must serve four workloads with different characteristics:

| Workload | Characteristic |
| --- | --- |
| Command-side writes | Aggregate integrity, invariants, transactional consistency |
| Management reads | Modest volume, varied shapes |
| Analytics aggregation | Hundreds of millions of rows (NFR-SCAL-007) |
| Ledger ingestion | High-throughput batch insert (ADR-0011) |

Cross-cutting persistence concerns must apply without exception: tenant context for
row-level security (ADR-0005), audit metadata, domain event collection, and outbox writes
(ADR-0013). If any of these depends on developer discipline, coverage will be incomplete —
and for the tenant interceptor, incomplete coverage is a security defect.

## 2. Problem Statement

What data access technology should be used for each workload, and how are cross-cutting
persistence concerns guaranteed to apply universally?

## 3. Decision

**Entity Framework Core is the primary technology, with a bounded exception for analytics
aggregation.**

| Path | Technology | Rationale |
| --- | --- | --- |
| Command-side writes | EF Core with change tracking | Aggregate integrity, unit of work, interceptors |
| Management reads | EF Core, no tracking, projected | Simplicity; volumes are modest |
| **Analytics aggregation** | **Direct SQL over projections** | Aggregations at NFR-SCAL-007 volume are not an ORM's strength |
| Ledger ingestion | Batched insert from the Worker | Row-by-row cannot meet throughput |
| Gateway hot path | **No relational access at all** | ADR-0010 |

**The transaction boundary is the command.** One command, one transaction, one commit. A
handler needing to affect another module commits its own work and publishes an integration
event (ADR-0013) — never a distributed transaction.

**Cross-cutting concerns live in interceptors**, not in handler code:

| Interceptor | Responsibility | Requirement |
| --- | --- | --- |
| **Tenant context** | Sets the session variable used by row-level security at connection checkout; **clears it at return** | NFR-SEC-007, ADR-0005 |
| Auditing metadata | Stamps creation and modification metadata | FR-AUD-002 |
| Domain event collection | Gathers events raised during the unit of work for post-commit dispatch | ADR-0013 |
| Outbox write | Persists integration events in the same transaction as the state change | ADR-0013 |
| Soft-delete filtering | Applies where the retention model requires reversible deletion | FR-TEN-013 |

**Additional binding rules:**

- **No lazy loading.** All loading is explicit — prevents surprise queries in
  latency-budgeted paths.
- **Money is a value object over decimal**, never a floating-point primitive. NFR-DATA-003's
  2% tolerance cannot survive representation error.
- **Queries may bypass the domain and read projections directly** (BD-002). Reconstructing
  aggregates to render a chart is waste.
- **Direct SQL is permitted for analytics aggregation only.** Permitting it generally
  would erode the model and, more importantly, would multiply the paths that must be
  reviewed for tenant safety.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Micro-ORM everywhere | Dapper or similar for all access | Faster and more explicit. Rejected: loses change tracking, unit of work, and — decisively — **interceptors**, which is how tenant context and outbox writes are guaranteed. Every query would need manual tenant handling, which is precisely the failure mode NFR-SEC-007 forbids |
| Raw ADO.NET | Full control | Same objection, more severely |
| EF Core for absolutely everything | No SQL exception | Aggregation over hundreds of millions of rows through an ORM produces poor plans and high materialization cost. NFR-PERF-010 would not be met |
| Repository pattern hiding EF entirely | Full abstraction over the ORM | Adds a layer that mostly re-implements EF's own abstractions. Repository *interfaces* live in `Domain` (ADR-0001); implementations may use EF directly |
| CQRS with separate read database | Physically separate read store from the start | Correct eventual destination for Analytics (ADR-0004 §9); premature now. Projections in the same database achieve most of the benefit |

## 5. Pros

- **Interceptors guarantee cross-cutting coverage** structurally rather than by discipline.
  This is the decisive argument: the tenant interceptor is a security control, and a
  security control that developers must remember to apply is not a control.
- **Change tracking and unit of work** support the aggregate-oriented domain model.
- **Migrations are a first-class, reviewable artifact** — important for the
  expand-and-contract discipline that rolling deployment requires.
- **Productivity on the command side**, where most of the 230 requirements live.
- The analytics exception is bounded and explicit rather than a general escape hatch.

## 6. Cons

- **Two idioms in one codebase** — EF for most things, SQL for analytics. This is a real
  cognitive cost and a source of inconsistency.
- **Interceptor behaviour is non-local**, making debugging harder: a value appears in a
  row and nothing in the handler explains it.
- **EF Core can generate poor queries** when used carelessly; explicit loading and
  projection discipline are required.
- **Direct SQL bypasses EF's global query filters**, relying entirely on database
  row-level security for tenant safety — which is exactly why ADR-0005 chose
  database-enforced isolation, but it means the analytics path has one layer of protection
  rather than two.
- **Migration discipline is demanding**: expand-and-contract across releases is more work
  than a simple schema change.

## 7. Consequences

- **The tenant interceptor's failure mode must be to set no tenant**, not to omit the
  constraint. Under ADR-0005 that yields zero rows — visible and safe — rather than
  unfiltered rows.
- **Connection pooling mode becomes a security decision** (ADR-0005 §7, decision DD-2). A
  pooled connection returned with a tenant variable still set, then reused by another
  Company's request, is a cross-tenant exposure. The interceptor must clear on return.
- **Direct SQL usage is restricted to Analytics projections and is a review gate.**
  Broadening it broadens the tenant-safety review surface.
- **Migrations must be backward-compatible with the previous application version**
  (ADR-0018 §7), because rolling deployment runs both concurrently.
- **Cross-module consistency is eventual** — one command, one transaction, never spanning
  modules.
- **Query handlers may need their own read contexts** as projections diverge from the write
  model; this is anticipated but not required at MVP.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | A developer bypasses the tenant interceptor with raw SQL, defeating one isolation layer | **Critical** | Medium | Row-level security still applies at the database; direct SQL restricted to Analytics and reviewed; architecture test on SQL usage location |
| R-2 | Interceptor fails to clear tenant context on connection return | **Critical** | Medium | Explicit clear-on-return; prototype and load-test before schema design (DD-2) |
| R-3 | An operation bypasses the dispatcher and therefore the interceptors | High | Medium | AT-10 — no repository invoked outside a dispatcher-mediated handler |
| R-4 | EF generates inefficient queries that breach NFR-PERF-016 | Medium | High | No lazy loading; explicit projection; query plan review for hot management paths |
| R-5 | Monetary values represented as floating point, breaching NFR-DATA-003 | High | Low | Money value object over decimal; architecture test on primitive usage in monetary contexts |
| R-6 | Migration not backward-compatible, breaking rolling deployment | High | Medium | Expand-and-contract mandatory; migration tested against the previous version in CI |

## 9. Future Revisions

Revisit when:

- **Analytics moves to a separate store** (ADR-0004 §9). The direct-SQL exception migrates
  with it, and the main codebase returns to a single idiom — a simplification worth
  noting as a benefit of that move.
- **Read models diverge enough to warrant separate read contexts** per module.
- **Ingestion throughput requires bulk-copy rather than batched insert.** This would be a
  further exception and should be recorded rather than assumed.
- **EF Core's row-level-security support changes materially.** Better first-class support
  would reduce the interceptor's responsibility and lower risk R-2.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §3.6 persistence approach; interceptor table |
| [`ADR-0005-multi-tenant-strategy.md`](ADR-0005-multi-tenant-strategy.md) | Tenant interceptor is the enforcement mechanism |
| [`ADR-0004-postgresql.md`](ADR-0004-postgresql.md) | The store |
| [`ADR-0013-outbox-eventing.md`](ADR-0013-outbox-eventing.md) | Outbox interceptor |
| [`ADR-0012-cqrs-dispatcher.md`](ADR-0012-cqrs-dispatcher.md) | Transaction behaviour position |
| [`ADR-0001-clean-architecture.md`](ADR-0001-clean-architecture.md) | Repository interfaces in `Domain` |
| [`ADR-0010-gateway-hot-path.md`](ADR-0010-gateway-hot-path.md) | Why the Gateway uses none of this |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-SEC-007, NFR-DATA-003, NFR-MAINT-007 |
| `../03-database/` | Phase 4 — schema realizing this approach |
