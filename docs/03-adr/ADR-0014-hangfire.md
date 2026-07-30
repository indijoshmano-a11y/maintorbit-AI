# ADR-0014 — Use Hangfire on PostgreSQL in a dedicated Worker host

| Field | Value |
| --- | --- |
| **ADR Number** | ADR-0014 |
| **Title** | Use Hangfire with PostgreSQL storage, running in a dedicated Worker host |
| **Status** | **Accepted** |
| **Date** | 2026-07-30 |
| **Deciders** | Engineering |
| **Implements** | AD-010, BD-001, BD-006 |
| **Supersedes** | — |

---

## 1. Context

Substantial work must happen outside the request path:

| Job | Cadence |
| --- | --- |
| Usage and audit batch persistence | Continuous, consumer group |
| Cost calculation | Follows persistence |
| Analytics projection | Follows cost |
| Outbox relay | Continuous |
| Model catalog refresh | Scheduled |
| Provider health probing | Scheduled, frequent |
| Notification delivery | Event-driven |
| Retention enforcement | Scheduled, daily |
| Reconciliation | Scheduled |

Two constraints shape the decision. **NFR-PERF-001** means batch work must not compete
with the Gateway for CPU or connection-pool capacity. **NFR-PORT-002** forbids any
dependency that cannot run in a customer-controlled environment.

## 2. Problem Statement

What should execute background and scheduled work, where should it run, and how is the
Gateway's latency budget protected from it?

## 3. Decision

**Hangfire with PostgreSQL storage, running in a dedicated Worker host built from the
same solution as the API host.**

| Aspect | Decision |
| --- | --- |
| Job framework | Hangfire |
| Storage | **PostgreSQL** — no additional infrastructure dependency |
| Process | **Dedicated Worker host**, separate container, separate from the API host |
| Code sharing | Same libraries as the API host; a distinct entry point, not a distinct solution |
| Queue partitioning | Named queues with dedicated worker allocation per job class |
| Idempotency | **Mandatory for every job** — Hangfire retries |
| Tenant context | Established explicitly from the job payload before any data access |

**The ingestion queue has its own worker allocation, protected from every other job
class.** If one Company's analytics projection rebuild shares a queue with usage
persistence, a large rebuild delays ingestion for every Company and degrades usage
freshness platform-wide.

**Separating API and Worker hosts is required from day one, not deferred.** It is the
partial recovery of the failure and resource isolation that ADR-0002 gave up by choosing
a monolith.

## 4. Alternatives Considered

| Alternative | Description | Why not chosen |
| --- | --- | --- |
| Background services inside the API host | .NET hosted services, no framework | Batch work competes directly with the Gateway for thread pool and connections — the exact thing NFR-PERF-001 cannot tolerate. Also no retry, scheduling, or visibility |
| Hangfire with Redis storage | Faster than polling PostgreSQL | Adds ledger-critical load to the Redis instance already carrying four roles (ADR-0006), and Redis durability is weaker than PostgreSQL for job state |
| Quartz.NET | Mature .NET scheduler | Strong scheduling; weaker fire-and-forget queueing and no comparable operational dashboard. Hangfire fits the mixed workload better |
| Broker-backed queue | Kafka, RabbitMQ, or NATS with custom workers | Better throughput ceiling and no polling. Rejected on operational surface and NFR-PORT-002 self-hosting burden at this stage |
| Cloud-managed job service | Azure Functions, or similar | **Violates NFR-PORT-002** outright |
| Separate Worker solution | Fully independent codebase | Handler logic would diverge between foreground and background paths over time — a known source of subtle inconsistency |

## 5. Pros

- **No additional infrastructure dependency** — PostgreSQL is already required, satisfying
  NFR-PORT-002 without qualification.
- **Job state is transactional and durable**, in the same store as the data jobs operate
  on.
- **The Gateway is protected** from batch CPU and connection consumption by process
  separation.
- **Shared libraries prevent divergence** between foreground and background handler logic
  (BD-001).
- **Built-in retry, scheduling, and an operational dashboard** — meaningful for the P-02
  persona, who needs to see what background work is doing.
- Queue partitioning gives per-job-class isolation without separate deployments.

## 6. Cons

- **PostgreSQL storage polls**, producing steady background query load. This is less
  efficient than a broker-backed queue and adds load to the store that is already the
  expected write bottleneck (ADR-0004 §6).
- **Throughput ceiling is lower** than a purpose-built broker. Acceptable at
  NFR-SCAL-002; questionable at ten times that volume.
- **At-least-once execution makes idempotency mandatory everywhere**, compounding the
  same requirement from ADR-0013.
- **The Hangfire dashboard is an administrative surface** that must be authenticated and
  authorized like any other, and it exposes job payloads.
- Worker deployment carries code it does not use, since it shares libraries with the API
  host.

## 7. Consequences

- **Every job must be idempotent.** Hangfire retries, and a job that is not safe to re-run
  will corrupt the ledger — precisely the data NFR-DATA-009 requires to be reproducible.
  This is a review gate.
- **Every job must establish tenant context explicitly** from its payload before any data
  access. There is no inbound request to derive it from, and under ADR-0005 a missing
  context yields no rows rather than unfiltered rows — safe, but silently confusing.
- **The Hangfire dashboard must be authenticated, authorized, and audited**, and should
  not be exposed publicly. Job payloads may contain identifiers that are sensitive in
  aggregate.
- **Queue partitioning is a design requirement**, not a tuning option. Ingestion must
  never queue behind analytics.
- **Polling load must be included in PostgreSQL capacity planning.** It is small per query
  and constant, which makes it easy to omit from estimates.
- **Worker host scaling is independent** of API host scaling and partitioned by job type.

## 8. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | A non-idempotent job corrupts the ledger on retry | High | Medium | Idempotency review gate for every job; deduplication by stable identifier; reconciliation detects divergence |
| R-2 | Analytics or projection work starves ingestion | High | Medium | Dedicated ingestion queue and worker allocation |
| R-3 | Polling load contributes to PostgreSQL becoming the bottleneck sooner | Medium | Medium | Included in capacity planning; poll interval tuned; broker reconsidered per §9 |
| R-4 | Hangfire dashboard exposed without adequate authorization | High | Low | Authenticated, authorized, audited, not publicly routed |
| R-5 | A job omits tenant context and processes nothing, misdiagnosed as a data problem | Medium | High | Explicit establishment is a job-authoring requirement; documented failure signature |
| R-6 | Throughput ceiling reached as request volume grows | Medium | Medium | Queue depth alerting; broker migration path per §9 |

## 9. Future Revisions

Revisit when:

- **Ingestion throughput approaches Hangfire's practical ceiling** on PostgreSQL storage —
  the signal is batch writer lag pushing usage freshness past 45 seconds (75% of
  NFR-PERF-013).
- **PostgreSQL write load becomes the binding constraint** and polling is a measurable
  contributor.
- **A broker is introduced for another reason** — ADR-0011's ingestion replacement or
  ADR-0013's extraction transport. If a durable log is added for either, moving job
  queueing onto it reduces total operational surface rather than increasing it. These
  three decisions should be evaluated together, not separately.
- **Module extraction begins.** An extracted service may run its own worker, and the
  shared-library argument (BD-001) no longer applies across a service boundary.

## 10. Related Documents

| Document | Relationship |
| --- | --- |
| [`../02-architecture/system-architecture.md`](../02-architecture/system-architecture.md) | AD-010; §3.2 container view |
| [`../02-architecture/backend-architecture-overview.md`](../02-architecture/backend-architecture-overview.md) | §3.7 background processing; job table |
| [`../02-architecture/scalability-strategy.md`](../02-architecture/scalability-strategy.md) | §3.7 worker queue partitioning |
| [`ADR-0004-postgresql.md`](ADR-0004-postgresql.md) | Job storage |
| [`ADR-0011-usage-audit-ingestion.md`](ADR-0011-usage-audit-ingestion.md) | The batch writer runs here |
| [`ADR-0013-outbox-eventing.md`](ADR-0013-outbox-eventing.md) | The relay runs here; shared idempotency requirement |
| [`ADR-0005-multi-tenant-strategy.md`](ADR-0005-multi-tenant-strategy.md) | Explicit tenant context in jobs |
| [`ADR-0022-deployment-topology.md`](ADR-0022-deployment-topology.md) | Worker container placement |
