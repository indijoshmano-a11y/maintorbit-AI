# ADR-0004 — Use PostgreSQL as the single system of record

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0004 |
| **Title** | Use PostgreSQL as the single system of record, with schema-per-module |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-002 (storage aspect), AD-010 |
| **Supersedes** | — |

---

## 1. Context

The platform must durably store: organizational structure, identity and credentials,
provider connections with encrypted secrets, routing and governance policies,
conversations, subscriptions, and — dominating everything else by volume — a ledger of
Usage Records, Cost Records, Audit Events, and Decision Records.

NFR-SCAL-007 requires **500 million Usage Records per Company** to remain queryable.
NFR-DATA-006 requires immutability. NFR-DATA-007 forbids sampling. NFR-SEC-007 requires
tenant isolation enforced *below* the application layer. NFR-PORT-002 forbids any
dependency that cannot run in a customer-controlled environment.

## 2. Problem Statement

What durable store can serve transactional organizational data, a high-volume immutable
ledger, and database-enforced tenant isolation — while remaining fully self-hostable?

## 3. Decision

Use **PostgreSQL** as the single system of record for all durable state.

| Aspect | Decision |
| --- | --- |
| Logical organization | **One schema per module**, supporting rule R-6 of ADR-0002 — no module holds a foreign key into another's schema |
| Tenant isolation | Row-level security with a session variable (ADR-0005) |
| Ledger tables | Time-partitioned; retention enforced by **partition drop**, never mass deletion |
| Analytics | Served primarily from pre-aggregated projections, not raw records |
| Hangfire storage | PostgreSQL, avoiding an additional infrastructure dependency (ADR-0014) |
| Read scaling | Read replicas for Analytics before any consideration of a different store |
| Naming | `snake_case` tables and columns per the Phase 0 conventions |

Redis is **not** a second system of record. It holds cache, counters, streams in transit,
and the SignalR backplane (ADR-0006) — all of which are either reconstructible or in
flight to PostgreSQL.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| SQL Server | Natural pairing with .NET; strong tooling | Licensing cost, and weaker self-hosting story for customers who would need their own licence. Row-level security exists but PostgreSQL's implementation is well-proven at this pattern |
| MySQL / MariaDB | Widely deployed, self-hostable | Weaker partitioning, weaker JSON handling, and no comparable row-level security implementation — which is the decisive requirement (NFR-SEC-007) |
| PostgreSQL for transactions + a time-series store for the ledger | Purpose-built store for 500 M records per Company | **The strongest alternative.** Rejected *for now* on operational surface: two stores to run, back up, secure, and self-host from day one. Deferred deliberately — see §9 |
| Document database | Flexible schema | The domain is highly relational — attribution chains, organizational hierarchy, permission evaluation. Cost accuracy at 2% tolerance wants transactional integrity |
| Cloud-managed proprietary store | Managed operations | Violates NFR-PORT-002 outright |

## 5. Pros

- **Row-level security satisfies NFR-SEC-007 literally** — isolation enforced below every
  query the application can construct. No other candidate offers this as maturely.
- **Partitioning makes retention cheap.** Dropping a partition is near-instantaneous;
  deleting hundreds of millions of rows is not.
- **One store to operate, back up, secure, and self-host.**
- **Transactional integrity** for cost calculation and organizational data.
- Fully self-hostable, satisfying NFR-PORT-002 without qualification.
- Mature EF Core support (ADR-0023) and a large operational knowledge base.

## 6. Cons

- **Not purpose-built for 500 million records per Company.** A columnar or time-series
  store would serve Analytics substantially better at that volume.
- **Row-level security has a query-planning cost** that must be measured; it may interact
  badly with partition pruning (ADR-0005 §8, R-2).
- **Write throughput is a known bottleneck** at approximately 200–400 sustained requests
  per second, appearing before API host CPU saturation.
- **Vertical scaling has a ceiling**; horizontal write scaling requires sharding, which
  is a significant undertaking.
- Hangfire's PostgreSQL storage polls, producing steady background query load.

## 7. Consequences

- **Analytics must be served from pre-aggregated projections**, not raw records.
  Attempting NFR-PERF-010 (3 s for a 30-day query) against 500 million raw rows will not
  succeed.
- **Projections are rebuildable and hold no authoritative state** (BD-007). This is what
  makes a future store substitution a rebuild rather than a data migration.
- **Batched writes are mandatory** for ledger ingestion (ADR-0011). Row-by-row insertion
  cannot meet the throughput.
- **Tiered retention preserves completeness affordably**: aged partitions are compressed
  and less-indexed but remain complete and queryable. This is the architectural answer to
  the conflict between efficiency goal G4.5 and the no-sampling constraint.
- **Connection pooling becomes a security decision**, not a performance one, because of
  the session-variable interaction with row-level security (ADR-0005 §7).
- **Schema-per-module must be maintained from the first migration.** Retrofitting it
  after tables exist is expensive and error-prone.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | Row-level security prevents partition pruning, making NFR-PERF-010 unreachable at volume | High | Medium | Prototype before schema design (blocking decision D-1); pre-aggregation reduces reliance on raw queries |
| R-2 | Write throughput becomes the binding constraint sooner than estimated | High | Medium | Batched writes; partitioning; stream depth alerting as the early signal |
| R-3 | Analytics query cost grows unacceptably as records accumulate | High | High | Projections; read replicas; tiering; store substitution planned per §9 |
| R-4 | Cross-module foreign keys are introduced, breaking ADR-0002 R-6 | High | Medium | Schema-per-module; architecture and migration review gate |
| R-5 | Retention implemented as mass deletion, producing bloat and vacuum load | Medium | Medium | Partition-aligned retention is a design requirement, recorded here and in Phase 4 schema work |
| R-6 | Single-instance PostgreSQL becomes an availability constraint | High | Medium | Primary with streaming standby from topology T1 (ADR-0022) |

## 9. Future Revisions

**A separate analytical store is expected, not hypothetical.** Revisit when:

- Analytics query p95 exceeds 2.2 seconds (75% of NFR-PERF-010), the defined scaling
  trigger; **or**
- Any Company exceeds roughly 100 million Usage Records; **or**
- Read replicas no longer isolate analytics load sufficiently.

The expected replacement is a columnar or time-series store fed by the same projection
pipeline. Because projections hold no authoritative state, this is a rebuild rather than
a migration — that property is the reason BD-007 exists.

**The replacement must remain self-hostable** (NFR-PORT-002). This constraint eliminates
several otherwise attractive managed options and should be applied at evaluation time,
not discovered afterwards.

Revisit sharding only if vertical scaling and read replicas are both exhausted. Sharding
a multi-tenant ledger is a substantial programme and should not be entered casually.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | §3.2 container view; AD-002 |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | §3.5 growth strategy; partitioning and tiering |
| [`ADR-0005-multi-tenant-strategy.md`](ADR-0005-multi-tenant-strategy.md) | Row-level security depends on this store |
| [`ADR-0011-usage-audit-ingestion.md`](ADR-0011-usage-audit-ingestion.md) | Batched write path into PostgreSQL |
| [`ADR-0023-persistence-ef-core.md`](ADR-0023-persistence-ef-core.md) | Access technology |
| [`ADR-0014-hangfire.md`](ADR-0014-hangfire.md) | Job storage in PostgreSQL |
| [`ADR-0006-redis.md`](ADR-0006-redis.md) | What Redis holds, and why it is not a second record |
| [`../01-product/non-functional-requirements.md`](../01-product/non-functional-requirements.md) | NFR-SCAL-007/008, NFR-DATA-006/007, NFR-SEC-007 |
