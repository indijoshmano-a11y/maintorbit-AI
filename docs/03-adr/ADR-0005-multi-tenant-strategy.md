# ADR-0005 — Tenant isolation by row-level security

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0005 |
| **Title** | Isolate tenants by shared database with PostgreSQL row-level security |
| **Status** | **Proposed** — ratification blocked on prototype (decision D-1) |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering, Security |
| **Implements** | AD-002 |
| **Supersedes** | — |

> **This is the highest-impact decision in the architecture.** It touches every query,
> every index, every migration, and every background job. It cannot be changed cheaply
> once schema work begins, and it is the decision most likely to be regretted if made
> carelessly. It is deliberately recorded as **Proposed** rather than Accepted: §9 states
> the prototype that must pass before ratification.

---

## 1. Context

MaintOrbit AI is multi-tenant. A **Company** is the tenant and the isolation boundary,
and no data may ever be visible across Companies (FR-TEN-001).

NFR-SEC-007 states the requirement precisely: isolation must be **enforced at the
data-access layer such that an application-layer defect cannot cause cross-tenant
exposure**. That wording is deliberate and rules out approaches that depend on
application code remembering to filter.

The stakes are unusually high. The platform stores customers' Provider Credentials —
secrets carrying direct spend authority and an unrestricted data egress channel. A
cross-tenant exposure here is existential rather than embarrassing, and the P-06 persona
who evaluates this treats detected overstatement as disqualifying.

Target scale is 500 Companies per deployment (NFR-SCAL-001) with 500 million Usage
Records per Company (NFR-SCAL-007).

## 2. Problem Statement

How should tenant data be isolated so that a forgotten `WHERE` clause, a hand-written
analytics query, or a background job with no inbound request context cannot leak one
Company's data to another?

## 3. Decision

**Single PostgreSQL database. One schema per module. Every tenant-scoped relation carries
a `company_id`. Isolation is enforced by PostgreSQL row-level security**, with the
current Company set as a session variable at connection checkout from the ambient tenant
context.

Two layers, deliberately redundant:

| Layer | Mechanism | Catches |
| --- | --- | --- |
| Application | Global query filter on tenant-scoped entities | Ordinary queries; provides good error behaviour and clear intent |
| **Database** | **Row-level security policy on every tenant-scoped relation** | **Everything the application layer misses — raw SQL, forgotten filters, defects** |

**The failure direction is correct by construction.** If the session variable is unset,
policies match nothing and queries return **no rows**. A missing tenant context produces
an empty result, never an unfiltered one. Failure is visible and safe rather than silent
and catastrophic.

**Platform-administrative operations** that legitimately span Companies require an
explicitly elevated database role, permitted only in named, reviewed, audited code paths.

## 4. Alternatives Considered

| Alternative | Isolation strength | Operational cost | Why not chosen |
| --- | --- | --- | --- |
| Discriminator column, application-enforced only | **Weak** — one missing filter leaks data | Lowest | **Violates NFR-SEC-007 explicitly.** This is the approach the requirement was written to exclude |
| **Discriminator + row-level security** | **Strong** — enforced by the database | Low | **Selected** |
| Schema per Company | Very strong | High — 500+ schemas, migration fan-out across all of them, connection and catalogue pressure | Rejected at target scale. Migration of 500 schemas is a fragile operation |
| Database per Company | Strongest | Very high — connection multiplication, 500 migration targets, backup fan-out | Rejected for multi-tenant hosting. **Reserved for self-hosted single-tenant deployment**, where it is the natural model |
| Separate deployment per Company | Absolute | Prohibitive | Only viable for a small number of very large customers |

## 5. Pros

- **Satisfies NFR-SEC-007 literally**, not approximately. An application defect cannot
  produce cross-tenant exposure because the check sits below every query the application
  can construct.
- **Protects the paths most likely to be got wrong**: hand-written analytics SQL,
  Hangfire jobs with no inbound request, and the outbox relay.
- **Safe failure direction** — unset context yields no rows.
- **One database to operate, migrate, back up, and self-host.**
- Scales to 500 Companies without per-tenant operational multiplication.
- Compatible with schema-per-module (ADR-0004) and therefore with ADR-0002's rule R-6.

## 6. Cons

- **Query-planning cost.** Row-level security policies are evaluated per query and may
  affect plan selection, particularly in combination with partitioning.
- **Interacts dangerously with connection pooling** — see §7. This is the single most
  serious operational hazard of the approach.
- **The elevated role is a hole in the guarantee.** Any code path using it operates
  without row-level protection.
- **Noisy-neighbour effects remain** at the storage layer; isolation is logical, not
  physical.
- Harder to reason about during debugging: a query returning nothing may be correct
  behaviour, a missing context, or a genuine absence of data.
- Restoring a single Company's data from backup is materially harder than with
  database-per-tenant.

## 7. Consequences

- **The tenant session variable must be set at connection checkout and cleared at
  connection return.** A pooled connection returned with a tenant variable still set, then
  handed to a request for a different Company, is a cross-tenant exposure. **Connection
  pooling mode selection therefore becomes a security decision, not a performance one** —
  transaction-level pooling and session-level state are not compatible without care.
- **Hangfire jobs must establish tenant context explicitly** from the job payload before
  any data access. There is no inbound request to derive it from.
- **The outbox relay and platform administration run elevated**, and each event handler
  must re-establish its own Company context.
- **SignalR group membership must derive from server-side context only**, never from a
  client-supplied value — a client able to name its own group could subscribe across
  tenants.
- **Every tenant-scoped entity must carry the discriminator**, verified by architecture
  test AT-4.
- **An architecture test should enumerate the code paths permitted to request
  elevation.** Unreviewed elevation is the residual risk of this design.
- Self-hosted single-tenant deployment (v2.1) can use the same schema with a single
  Company, or drop to database-per-tenant naturally.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Connection pooling leaks tenant context between requests | **Critical** | Medium | Set at checkout, clear at return; pooling mode chosen as a security decision; **prototype required before ratification** |
| R-2 | Row-level security prevents partition pruning, breaching NFR-PERF-010 at NFR-SCAL-007 volume | High | Medium | Prototype at target volume; pre-aggregated projections reduce reliance on raw queries |
| R-3 | A code path using the elevated role leaks cross-tenant data | **Critical** | Medium | Elevation restricted to named paths; architecture test enumerating them; every use audited |
| R-4 | A background job omits tenant context and the empty result is misread as a data problem | Medium | High | Explicit context establishment is a job-authoring requirement; the safe failure direction limits harm to confusion |
| R-5 | Query-planning cost degrades management-path performance | Medium | Medium | Measured in prototype; indexes designed with policies in place, not added afterwards |
| R-6 | Policies are added inconsistently as new tables appear | High | Medium | Policy creation is part of the migration template; a test asserts every tenant-scoped table has a policy |

## 9. Ratification criteria — what must pass before Status becomes Accepted

This ADR remains **Proposed** until a prototype demonstrates, with recorded results:

1. **Pooling safety** — under a connection pooler in the intended mode, no request
   observes another Company's tenant context across at least 10⁶ pooled checkouts under
   concurrent multi-tenant load.
2. **Partition pruning holds** — an analytics query over a 30-day range against a
   partitioned ledger table with policies enabled prunes partitions and completes within
   NFR-PERF-010 at representative volume.
3. **Management-path overhead is acceptable** — policy evaluation does not push
   management operations beyond NFR-PERF-016 (300 ms p95).
4. **Failure direction confirmed** — with the session variable unset, every tenant-scoped
   relation returns zero rows, verified per table.

If criterion 1 fails, the pooling strategy changes — not the isolation strategy. If
criterion 2 fails, the response is pre-aggregated projections, **not** abandoning
database-enforced isolation.

## 10. Future Revisions

Revisit if:

- **A customer requires physical isolation.** Regulated enterprises (segment 3.2) may
  demand database-per-tenant contractually. This is already the expected model for
  self-hosted deployment and does not invalidate the shared-database approach for
  multi-tenant hosting.
- **A single Company's volume dominates the deployment**, making its data a candidate for
  its own database on operational grounds.
- **A parent-organization construct is introduced** (FR-TEN-016). Hierarchy above Company
  would change what "the tenant" means and would require this ADR to be amended.
- **PostgreSQL is no longer the sole store.** If an analytical store is introduced
  (ADR-0004 §9), it must provide equivalent isolation. This is a hard evaluation
  criterion, not a preference — a store without enforceable isolation cannot hold
  tenant data regardless of its other merits.

## 11. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-002; §6 risk R-1; §8 decision D-1 |
| [`../02-architecture/authentication-architecture.md`](../02-architecture/authentication-architecture.md) | §3.5 tenant context resolution and enforcement |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | §3.6 pooling interaction; decision DD-2 |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | §3.5 partitioning interaction |
| [`ADR-0004-postgresql.md`](ADR-0004-postgresql.md) | The store this depends on |
| [`ADR-0023-persistence-ef-core.md`](ADR-0023-persistence-ef-core.md) | Tenant interceptor setting the session variable |
| [`ADR-0007-authentication-strategy.md`](ADR-0007-authentication-strategy.md) | Where tenant context originates |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-SEC-007/008 |
| [`../01-product/product-requirements.md`](../01-product/product-requirements.md) | FR-TEN-001/002, FR-PERM-007 |
