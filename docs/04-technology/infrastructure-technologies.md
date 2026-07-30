# Infrastructure Technologies

| Field | Value |
| --- | --- |
| Document | Infrastructure Technologies |
| Version | 1.0 |
| Status | Draft — **contains a licence finding requiring decision (TD-2)** |
| Owner | Engineering & Operations |
| Last updated | 2026-07-30 |
| Audience | Engineering, Operations, Security, Legal |
| Phase | 4 — Technology Standards |

---

## 1. Purpose

This document inventories every infrastructure dependency: data stores, edge, container
runtime, hosting, and CI/CD. For each it records why it was chosen, how long it is
supported, and what would replace it.

Infrastructure choices are the hardest to reverse and the most likely to be inherited
without examination. **NFR-PORT-002 applies with particular force here** — every component
listed must be runnable by a customer in their own environment, because v2.1 self-hosted
deployment depends on it.

---

## 2. Scope

**In scope:** PostgreSQL, Redis/Valkey, Nginx, Docker, Azure VMs, GitHub Actions, object
storage, and the telemetry backend.

**Out of scope:** application packages (`backend-technologies.md`,
`frontend-technologies.md`), external SaaS (`third-party-services.md`), topology and
sizing ([`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md)).

---

## 3. PostgreSQL

| Field | Value |
| --- | --- |
| **Purpose** | Single system of record — organizational data, identity, encrypted credentials, policies, conversations, subscriptions, Hangfire job state, and the ledger of Usage, Cost, Audit, and Decision records |
| **Why chosen** | **Row-level security is the decisive factor.** NFR-SEC-007 requires isolation enforced below the application layer, and no other self-hostable candidate offers it as maturely. Partitioning makes retention a partition drop rather than a mass deletion. Transactional integrity supports the 2% cost accuracy tolerance. Fully self-hostable |
| **Alternatives considered** | SQL Server — licensing cost, weaker self-hosting story for customers. MySQL/MariaDB — weaker partitioning and no comparable row-level security, which is disqualifying. Document database — the domain is highly relational. Time-series store for the ledger — the strongest alternative, deferred rather than rejected (ADR-0004 §9) |
| **Version** | **17.x or 18.x** — decision TD-5. Prefer the most recent major with at least a year of production track record |
| **Support lifecycle** | Five years per major from release; one major per year. A major upgrade is required roughly every 4–5 years and must be planned |
| **Risks** | Row-level security may prevent partition pruning at NFR-SCAL-007 volume (ADR-0005 R-2); write throughput is the expected first bottleneck at ~200–400 req/s; **connection pooling interacts dangerously with the session-variable mechanism** |
| **Upgrade strategy** | Minor versions applied in the monthly maintenance window. Major upgrades planned with a rehearsed procedure; standby promotion limits downtime. Never allow a major to reach end of life in production |
| **Replacement strategy** | Not anticipated for transactional data. **Analytics is expected to move** to a columnar or time-series store — a projection rebuild rather than a migration, which is why ADR-0023 BD-007 keeps Analytics free of authoritative state. Any replacement must provide equivalent tenant isolation; a store without it cannot hold tenant data regardless of other merits |
| **Security considerations** | Row-level security is the primary tenant isolation control. **The elevated role that bypasses it is the residual risk** — restricted to named, reviewed, audited paths. Encryption at rest via disk encryption; TLS in transit enforced. **The tenant session variable must be cleared on connection return** |
| **Performance considerations** | Time-partitioned ledger tables; batched writes; pre-aggregated projections serve most analytics; read replicas isolate analytics load. Hangfire's polling adds constant background query load that is easy to omit from capacity estimates |
| **Cross references** | [ADR-0004](../03-adr/ADR-0004-postgresql.md), [ADR-0005](../03-adr/ADR-0005-multi-tenant-strategy.md), [ADR-0023](../03-adr/ADR-0023-persistence-ef-core.md) |

### 3.1 Supporting components

| Component | Purpose | Notes |
| --- | --- | --- |
| Connection pooler | Bound connection count across API and Worker hosts | **Pooling mode is a security decision** (DD-2), not a performance one — transaction-level pooling and session-level state are not compatible without care |
| Streaming replication | Standby for failover from topology T1 | NFR-AVAIL-013 |
| Continuous archiving | Point-in-time recovery to within RPO ≤ 5 min | NFR-DR-001 |

---

## 4. Redis or Valkey — a licence decision

> **This section contains the second material finding of Phase 4.** See
> [`technology-stack.md`](technology-stack.md) §5.3 and decision TD-2.

| Field | Value |
| --- | --- |
| **Purpose** | Four distinct roles: hot-path cache, atomic quota and budget counters, durable ingestion streams, SignalR backplane |
| **Why chosen** | Sub-millisecond reads make ADR-0010's 15 ms budget achievable; atomic operations give correct enforcement across instances without coordination; streams with consumer groups provide durable append with pending-entry tracking; one technology serves all four roles |
| **Alternatives considered** | Four purpose-built technologies — four systems to operate and self-host. In-process cache only — cannot share counters or circuit state. Message broker for ingestion — stronger durability, more operational surface (deferred, ADR-0011 §10). PostgreSQL for counters — cannot meet the 5 ms budget |
| **Version** | Valkey 8.x recommended; Redis 7.x/8.x is the alternative |
| **Support lifecycle** | Rolling for both; no fixed end-of-support dates |
| **Risks** | **Licence risk — see below.** Also: single point of failure for the Gateway through two paths; AOF per-second sync leaves a bounded loss window; **the eviction policy conflict** |
| **Upgrade strategy** | Minor and patch in maintenance windows; replica-first with promotion |
| **Replacement strategy** | Protocol compatibility means Redis and Valkey are mutually substitutable **without a client change**. The ingestion role specifically may move to a durable log (ADR-0011 §10) |
| **Security considerations** | TLS in transit; authentication required; never exposed outside the private network; **credentials in the cache and counter data are identifiers, not secrets** — Provider Credentials are never cached in plaintext |
| **Performance considerations** | Charged against NFR-PERF-007/008. Role separation is staged; **stage 1 is required before production traffic for correctness, not capacity** |
| **Cross references** | [ADR-0006](../03-adr/ADR-0006-redis.md), [ADR-0021](../03-adr/ADR-0021-fail-open-fail-closed.md) |

### 4.1 The licence finding

| Fact | Detail | Verify at |
| --- | --- | --- |
| Redis relicensed in 2024 | Moved from BSD to a dual source-available model (RSALv2 / SSPLv1) — **not OSI-approved open source** | Redis licence page |
| Redis later added AGPLv3 | AGPLv3 offered as an additional option from Redis 8 | Same |
| Valkey | BSD-3-clause fork under the Linux Foundation, protocol-compatible | Valkey project |

**Why this matters here specifically.** MaintOrbit AI is a commercial closed-source
product that, from v2.1, ships to customers to run in their own environments
(NFR-PORT-007). Bundling or requiring a source-available or AGPL-licensed component in a
redistributed product raises questions that a purely hosted service would not face.

**Assessment:**

| Option | Licence risk | Capability difference | Migration cost |
| --- | --- | --- | --- |
| **Valkey** | **Low** — BSD-3, permissive, foundation-governed | None material for our four roles | — |
| Redis (AGPLv3) | Medium — copyleft; obligations need legal review before redistribution | — | Low (protocol-compatible) |
| Redis (source-available) | Medium-high — redistribution terms need legal review | — | Low |

**Recommendation: standardize on Valkey.** Every capability
[ADR-0006](../03-adr/ADR-0006-redis.md) relies on — atomic counters, streams with
consumer groups, pub/sub for the backplane, AOF persistence — is present in both, and
`StackExchange.Redis` speaks to either without change. The decision costs nothing now and
removes a legal question from the v2.1 path.

**This is decision TD-2.** It should involve legal, not only engineering.

### 4.2 Configuration that is correctness, not tuning

| Setting | Requirement | Consequence if wrong |
| --- | --- | --- |
| **Eviction policy on the streams instance** | **None** — no eviction permitted | An evicted stream entry is a **permanently lost Usage Record or Audit Event**, breaching NFR-DATA-001/002 with no error and no alert |
| Eviction on the cache instance | Permitted | Cache entries are reconstructible |
| AOF persistence | Enabled, per-second sync | Bounds the ingestion loss window (ADR-0011 §7) |
| Replication | Primary + replica with automatic failover from T1 | Without it, a Redis restart is a full Gateway outage |

**These four lines are the most consequential configuration in the infrastructure.** The
eviction distinction in particular cannot be expressed within a single instance, which is
why role separation stage 1 precedes production traffic.

---

## 5. Nginx

| Field | Value |
| --- | --- |
| **Purpose** | TLS termination, request routing, static asset serving, connection limits, security headers |
| **Why chosen** | Proven, small operational surface, permissively licensed, self-hostable; [`../01-product/mission.md`](../01-product/mission.md) §4.6 argues for boring choices at the edge |
| **Alternatives considered** | Caddy — automatic TLS is attractive, smaller operational track record. Traefik — better with dynamic orchestration than with static Compose. HAProxy — excellent load balancing, weaker static serving. Envoy — powerful, disproportionate operational complexity |
| **Version** | 1.28.x stable branch |
| **Support lifecycle** | Roughly annual stable branches; security patches backported |
| **Risks** | Single point of failure per host; **two configuration details are easy to get wrong and both are load-bearing** |
| **Upgrade strategy** | Stable branch; security patches promptly; annual branch upgrade in a maintenance window |
| **Replacement strategy** | Straightforward — configuration, not application coupling. Replacement would follow an orchestration change (ADR-0018 §9) |
| **Security considerations** | Current TLS protocol versions only, obsolete disabled (NFR-SEC-001); security headers applied uniformly including CSP (NFR-SEC-018); **access logs must never contain credentials or content** (NFR-OBS-009) |
| **Performance considerations** | See below — both items directly affect NFR-PERF |
| **Cross references** | [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) §3.5 |

### 5.1 Two configuration details that are load-bearing

**Response buffering must be disabled on streaming paths.** Nginx buffers responses by
default. On a streaming inference path this holds chunks until a buffer fills — destroying
NFR-PERF-004 (50 ms to first token) and NFR-PERF-005 (5 ms per chunk), and making
streaming appear not to work at all. This is a common and confusing failure.

**Nginx timeouts must exceed application timeouts.** If Nginx times out first, the client
receives a generic gateway error instead of the application's normalized, actionable error
— defeating FR-GW-006 and FR-X-001 at the last hop.

---

## 6. Docker and Docker Compose

| Field | Value |
| --- | --- |
| **Purpose** | Container runtime and orchestration for all topologies |
| **Why chosen** | NFR-PORT-001 requires containers; Compose is simple to operate and to reason about, which matters for a small team and for customers who must run it themselves. The single-host topology satisfies NFR-PORT-004 and doubles as the self-hosted evaluation product |
| **Alternatives considered** | Kubernetes — better at scale, substantial operational commitment for benefits appearing only at higher instance counts (**expected eventual destination**). Podman — compatible, smaller ecosystem. Swarm — declining investment. Direct VM deployment — violates NFR-PORT-001 |
| **Version** | Docker Engine 27.x or later; Compose v2 |
| **Support lifecycle** | Rolling; no long-term support branches |
| **Risks** | No automatic rescheduling on host failure; manual scaling; rolling deployment is scripted rather than orchestrated; Compose becomes limiting beyond a handful of instances |
| **Upgrade strategy** | Engine upgrades in host maintenance windows, one host at a time |
| **Replacement strategy** | Kubernetes when operational pain justifies it, **not on schedule**. Whatever is chosen must remain self-hostable — and note that requiring a Kubernetes cluster raises the bar for self-hosted customers considerably, so the Compose single-host topology should be retained as the evaluation path regardless |
| **Security considerations** | All containers non-root with read-only root filesystems where practical; explicit health checks; secrets injected at start, never baked into images; image scanning on every build |
| **Performance considerations** | Image size and startup time affect deployment duration and therefore the availability budget |
| **Cross references** | [ADR-0018](../03-adr/ADR-0018-docker.md) |

### 6.1 Base images

| Image | Purpose | Notes |
| --- | --- | --- |
| .NET runtime (Alpine or Debian slim) | API and Worker hosts | Chosen for size and patch cadence; **must match the runtime decision in TD-1** |
| Node LTS (Alpine or Debian slim) | Next.js server | Must match TD-1 |
| Nginx stable | Edge | |
| PostgreSQL | Development and single-host topologies | Managed service in the hosted deployment |
| Valkey or Redis | Development and single-host topologies | Per TD-2 |
| S3-compatible object store | Development, CI, self-hosted | **The CI default** — this is what keeps NFR-PORT-002 true |

**Base images must be rebuilt on a schedule, not only when application code changes.**
A container image accumulates operating-system vulnerabilities between rebuilds even when
nothing in our code has moved.

---

## 7. Azure Virtual Machines

| Field | Value |
| --- | --- |
| **Purpose** | Hosting for the vendor-operated deployment |
| **Why chosen** | Selected in Phase 0. VMs rather than a managed container platform keep the deployment model close to what a self-hosted customer runs, which reduces divergence between our environment and theirs |
| **Alternatives considered** | Managed container services — less operational burden, more divergence from the self-hosted path. Other clouds — no decisive difference; the architecture is portable by construction. Bare metal — disproportionate |
| **Version** | General-purpose or compute-optimized series; **sizing is a Phase 5 deliverable** pending the hot-path prototype |
| **Support lifecycle** | Rolling; VM series are periodically retired with migration notice |
| **Risks** | **A single VM cannot meet NFR-AVAIL-001** — see [ADR-0022](../03-adr/ADR-0022-deployment-topology.md) §7; vendor coupling for networking and load balancing; VM series retirement requires migration |
| **Upgrade strategy** | OS patching automated with maintenance windows and sequential draining; VM series migration when retirement is announced |
| **Replacement strategy** | The architecture is cloud-agnostic by construction (NFR-PORT-002). Moving clouds is an infrastructure exercise, not an application change — which is a genuine benefit of the portability constraint |
| **Security considerations** | Private virtual network; only the load balancer publicly addressable; **no public SSH** — bastion or just-in-time access with MFA, audited (NFR-SEC-013) |
| **Performance considerations** | The Gateway is CPU-sensitive; compute-optimized sizing for API hosts. Premium SSD with a separate data disk for stateful services |
| **Cross references** | [ADR-0022](../03-adr/ADR-0022-deployment-topology.md) |

**Infrastructure should be defined as code from the first deployment** (decision DD-5).
Manually configured VMs cannot be reproduced reliably, which undermines both disaster
recovery and the self-hosted path.

---

## 8. Object storage

Covered in full by [ADR-0017](../03-adr/ADR-0017-object-storage.md). Summary:

| Field | Value |
| --- | --- |
| **Purpose** | Generated exports, invoice documents, chat attachments (v1.1), cold-tier ledger archive, backup artifacts |
| **Why chosen** | An S3-compatible **port** with two adapters: Azure Blob for hosted, a self-hostable S3-compatible server as the portable default. The product depends on the standard, not on a vendor |
| **Version** | S3-compatible API |
| **Support lifecycle** | Rolling for both implementations |
| **Risks** | The portable implementation rots if only the Azure path is exercised — **assessed as high likelihood** |
| **Upgrade strategy** | Independent per implementation |
| **Replacement strategy** | Any S3-compatible store |
| **Security considerations** | Objects never public; time-limited, single-object signed URLs; **object path is not an authorization mechanism** — the application authorizes before issuing a URL; Company-scoped keys |
| **Performance considerations** | Exports generated asynchronously by the Worker and streamed to storage, never assembled in a request |

---

## 9. GitHub Actions

| Field | Value |
| --- | --- |
| **Purpose** | CI/CD — build, test, the architecture and security gates, image publication, deployment |
| **Why chosen** | Co-located with the source, so pull-request gating is native. **The gating checks are the primary value** — ADR-0001 and ADR-0002 are conventions without them |
| **Alternatives considered** | Azure DevOps — splits tooling across two platforms. Self-hosted (Jenkins, TeamCity) — infrastructure to operate for a small team. GitLab CI — would require moving source hosting |
| **Version** | Hosted service; runner images versioned |
| **Support lifecycle** | Rolling; runner images deprecated with notice |
| **Risks** | Vendor coupling — workflows are not portable; hosted runner cost grows with build frequency; **build time is a delivery constraint** against NFR-MAINT-009's 15-minute target |
| **Upgrade strategy** | Runner image and action version updates batched; pinned by SHA for third-party actions |
| **Replacement strategy** | Keep build logic in scripts rather than in workflow YAML where practical, so the workflow file is a thin invocation layer and migration is tractable |
| **Security considerations** | **Third-party actions pinned by commit SHA, never by tag** — a mutable tag is a supply-chain vector. Deployment credentials least-privilege and rotated. Secrets in GitHub's store are part of the deployment trust boundary — though **never Provider Credentials**, which exist only encrypted in the database |
| **Performance considerations** | Parallelization, caching, and selective execution by changed path are ongoing work against the build-time budget |
| **Cross references** | [ADR-0019](../03-adr/ADR-0019-github-actions.md) |

---

## 10. Telemetry backend

| Field | Value |
| --- | --- |
| **Purpose** | Collection and storage of traces, metrics, and logs |
| **Why chosen** | **Deliberately unspecified.** [ADR-0020](../03-adr/ADR-0020-observability.md) requires vendor-neutral instrumentation via OpenTelemetry precisely so the backend is a deployment choice rather than an application dependency |
| **Alternatives considered** | Any OTLP-compatible backend. The self-hosted stack — Prometheus, Grafana, Loki, Tempo — is the portable default and is already scaffolded in `docker/monitoring/` |
| **Version** | Per component |
| **Support lifecycle** | Per component |
| **Risks** | Telemetry volume and cost grow with request volume; a commercial backend chosen for our hosting must not become an application dependency |
| **Replacement strategy** | Configuration change only — this is the entire point of the OpenTelemetry decision |
| **Security considerations** | Telemetry must never contain credentials or content (NFR-OBS-009); per-Company metrics must not expose cross-tenant data (NFR-OBS-010) |
| **Cross references** | [ADR-0020](../03-adr/ADR-0020-observability.md) |

---

## 11. Infrastructure dependency inventory

| Dependency | Class | Self-hostable | Licence | Criticality | Risk |
| --- | --- | --- | --- | --- | --- |
| PostgreSQL | Data store | ✅ | PostgreSQL (permissive) | **Critical** | 🟢 |
| Valkey *(or Redis)* | Data store | ✅ | BSD-3 *(or see §4.1)* | **Critical** | 🟡 |
| Connection pooler | Data tier | ✅ | Permissive | High | 🟡 |
| Nginx | Edge | ✅ | BSD-2 | High | 🟢 |
| Docker Engine | Runtime | ✅ | Apache 2.0 | High | 🟢 |
| Docker Compose | Orchestration | ✅ | Apache 2.0 | High | 🟢 |
| S3-compatible object store | Storage | ✅ | Apache 2.0 | Moderate | 🟢 |
| Azure Blob Storage | Storage — hosted only | ❌ | Proprietary | Moderate | 🟡 |
| Azure Virtual Machines | Hosting — hosted only | ❌ | Proprietary | High | 🟡 |
| Azure Load Balancer | Hosting — hosted only | ❌ | Proprietary | High | 🟡 |
| Azure Key Vault | Key custodian — hosted only | ❌ | Proprietary | High | 🟡 |
| GitHub Actions | CI/CD — not shipped | ❌ | Proprietary | Moderate | 🟡 |
| OTLP telemetry backend | Observability | ✅ | Varies | Moderate | 🟢 |

**Every ❌ is either operational-only or behind a port.** Azure VMs, Load Balancer, and
GitHub Actions are our operational choices and are never shipped to customers. Azure Blob
and Key Vault are shipped-code dependencies **only through their ports**
([ADR-0017](../03-adr/ADR-0017-object-storage.md),
[ADR-0008](../03-adr/ADR-0008-credential-encryption.md)), with portable implementations as
the CI default. **AT-12 enforces that no direct reference escapes an adapter** — this is
the check that keeps the ❌ column from becoming a v2.1 problem.

---

## 12. Risks

| # | Risk | Severity | Likelihood | Mitigation |
| --- | --- | --- | --- | --- |
| R-1 | **Redis licence constrains v2.1 redistribution** | High | Medium | Decision TD-2 — Valkey |
| R-2 | Shared Redis instance evicts stream entries, silently losing ledger data | **Critical** | Medium | No eviction on streams; role separation before production traffic; memory alerting |
| R-3 | Connection pooling leaks tenant context between requests | **Critical** | Medium | Clear on return; pooling mode chosen as a security decision (DD-2); prototype before Phase 5 |
| R-4 | Single-VM deployment cannot meet NFR-AVAIL-001 | **Critical** | High | Decision DD-1 — two-host topology or an amended target |
| R-5 | Nginx buffering silently breaks streaming | High | Medium | Explicit configuration; integration test asserting chunk timing |
| R-6 | Portable object storage and key custodian rot | Medium | **High** | Portable implementations are the CI default, not alternatives |
| R-7 | Base images accumulate vulnerabilities between rebuilds | Medium | High | Scheduled rebuilds independent of code changes; image scanning |
| R-8 | PostgreSQL major upgrade deferred until forced | Medium | Medium | Calendar with six months' notice; rehearsed procedure |
| R-9 | Third-party GitHub Action compromised via a mutable tag | High | Low | Pin by commit SHA |
| R-10 | Azure VM series retirement forces unplanned migration | Low | Medium | Infrastructure as code makes migration tractable (DD-5) |

---

## 13. Cross references

| Document | Relationship |
| --- | --- |
| [`technology-stack.md`](technology-stack.md) | Master inventory; both findings |
| [`backend-technologies.md`](backend-technologies.md) | Clients for these components |
| [`support-lifecycle.md`](support-lifecycle.md) | End-of-support calendar |
| [`dependency-policy.md`](dependency-policy.md) | Licence policy applied in §4.1 |
| [`third-party-services.md`](third-party-services.md) | External SaaS, distinct from infrastructure |
| [`../03-adr/ADR-0004-postgresql.md`](../03-adr/ADR-0004-postgresql.md) | Data store decision |
| [`../03-adr/ADR-0006-redis.md`](../03-adr/ADR-0006-redis.md) | Four-role decision |
| [`../03-adr/ADR-0018-docker.md`](../03-adr/ADR-0018-docker.md) | Container strategy |
| [`../03-adr/ADR-0022-deployment-topology.md`](../03-adr/ADR-0022-deployment-topology.md) | Topology and availability |
| [`../02-architecture/deployment-architecture.md`](../02-architecture/deployment-architecture.md) | How these are deployed |
